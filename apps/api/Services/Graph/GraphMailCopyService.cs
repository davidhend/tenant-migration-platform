using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;

namespace MigrationPlatform.Api.Services.Graph;

public sealed class GraphMailCopyService : IGraphMailCopyService
{
    private readonly ILogger<GraphMailCopyService> _logger;
    private readonly IConfiguration _configuration;

    private const int DefaultPageSize = 50;
    private const int ThrottleCooldownMs = 60_000;

    private int _requestCount;
    private DateTime _requestWindowStart = DateTime.UtcNow;

    public GraphMailCopyService(
        ILogger<GraphMailCopyService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>Mutable per-user copy counters shared across folder passes.</summary>
    private sealed class CopyCounters
    {
        public int Failed;
        public int AttachmentsSkipped;
        public string? FirstError;

        public void RecordFailure(string error)
        {
            Failed++;
            FirstError ??= error;
        }
    }

    public async Task<MailCopyResult> CopyUserMailAsync(
        GraphServiceClient sourceClient,
        GraphServiceClient targetClient,
        string sourceUserId,
        string targetUserId,
        string? targetFolderName,
        Action<MailCopyProgress> onProgress,
        CancellationToken ct)
    {
        var pageSize = _configuration.GetValue("GraphMigration:Mail:MessagePageSize", DefaultPageSize);
        var maxConcurrent = _configuration.GetValue("GraphMigration:Mail:MaxConcurrentMessagesPerUser", 4);
        var throttle = new SemaphoreSlim(maxConcurrent);
        var counters = new CopyCounters();
        _requestCount = 0;
        _requestWindowStart = DateTime.UtcNow;
        var maxRequestsPer10Min = _configuration.GetValue("GraphMigration:ThrottleRequestsPerTenantPer10Min", 9000);

        // Enumerate source folders
        var sourceFolders = await EnumerateAllFoldersAsync(sourceClient, sourceUserId, ct);
        _logger.LogInformation(
            "GraphMailCopy: found {FolderCount} folders for {UserId}.",
            sourceFolders.Count, sourceUserId);

        // Count total messages across all folders
        var totalMessages = 0;
        foreach (var sf in sourceFolders)
            totalMessages += sf.TotalItemCount ?? 0;

        var progress = new MailCopyProgress(0, sourceFolders.Count, 0, totalMessages);
        onProgress(progress);

        // Create target root folder if specified
        string? targetRootFolderId = null;
        if (!string.IsNullOrWhiteSpace(targetFolderName))
        {
            targetRootFolderId = await GetOrCreateFolderAsync(
                targetClient, targetUserId, null, targetFolderName, ct);
        }

        // Build target folder structure and copy messages
        var folderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var foldersCopied = 0;
        var messagesCopied = 0;

        foreach (var sourceFolder in sourceFolders)
        {
            ct.ThrowIfCancellationRequested();

            var sourceFolderId = sourceFolder.Id!;
            var folderName = sourceFolder.DisplayName ?? "Unknown";

            // Skip well-known folders that shouldn't be copied
            if (IsSystemFolder(folderName))
            {
                _logger.LogDebug("GraphMailCopy: skipping system folder '{FolderName}'.", folderName);
                foldersCopied++;
                progress = progress with { FoldersCopied = foldersCopied };
                onProgress(progress);
                continue;
            }

            // Determine target folder
            string targetFolderId;
            if (sourceFolder.ParentFolderId is not null &&
                folderMap.TryGetValue(sourceFolder.ParentFolderId, out var mappedParent))
            {
                targetFolderId = await GetOrCreateFolderAsync(
                    targetClient, targetUserId, mappedParent, folderName, ct);
            }
            else
            {
                // Map well-known source folders to well-known target folders
                var wellKnown = MapToWellKnownFolder(folderName);
                if (wellKnown is not null)
                {
                    targetFolderId = await GetWellKnownFolderIdAsync(
                        targetClient, targetUserId, wellKnown, ct)
                        ?? await GetOrCreateFolderAsync(
                            targetClient, targetUserId, targetRootFolderId, folderName, ct);
                }
                else
                {
                    targetFolderId = await GetOrCreateFolderAsync(
                        targetClient, targetUserId, targetRootFolderId, folderName, ct);
                }
            }

            folderMap[sourceFolderId] = targetFolderId;

            // Copy messages in this folder
            var messagesInFolder = await CopyFolderMessagesAsync(
                sourceClient, targetClient,
                sourceUserId, targetUserId,
                sourceFolderId, targetFolderId,
                pageSize, throttle, counters,
                () => ThrottleCheckAsync(maxRequestsPer10Min, ct),
                ct);

            messagesCopied += messagesInFolder;
            foldersCopied++;
            progress = new MailCopyProgress(foldersCopied, sourceFolders.Count, messagesCopied, totalMessages);
            onProgress(progress);

            _logger.LogInformation(
                "GraphMailCopy: copied folder '{FolderName}' — {MsgCount} messages. " +
                "Progress: {FoldersCopied}/{TotalFolders} folders, {MsgCopied}/{TotalMsg} messages.",
                folderName, messagesInFolder, foldersCopied, sourceFolders.Count,
                messagesCopied, totalMessages);
        }

        if (counters.Failed > 0 || counters.AttachmentsSkipped > 0)
            _logger.LogWarning(
                "GraphMailCopy: finished {UserId} with gaps — {Failed} message(s) failed, " +
                "{AttSkipped} attachment(s) skipped. First error: {FirstError}",
                sourceUserId, counters.Failed, counters.AttachmentsSkipped, counters.FirstError);

        return new MailCopyResult(
            MessagesCopied: messagesCopied,
            TotalMessages: totalMessages,
            MessagesFailed: counters.Failed,
            AttachmentsSkipped: counters.AttachmentsSkipped,
            FirstError: counters.FirstError);
    }

    private async Task<List<MailFolder>> EnumerateAllFoldersAsync(
        GraphServiceClient client, string userId, CancellationToken ct)
    {
        var allFolders = new List<MailFolder>();
        var response = await client.Users[userId].MailFolders
            .GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = 100;
                cfg.QueryParameters.Select = new[] { "id", "displayName", "parentFolderId", "totalItemCount", "childFolderCount" };
                cfg.QueryParameters.IncludeHiddenFolders = "true";
            }, ct);

        while (response?.Value is not null)
        {
            allFolders.AddRange(response.Value);

            // Recurse into child folders
            foreach (var folder in response.Value.Where(f => (f.ChildFolderCount ?? 0) > 0))
                await EnumerateChildFoldersAsync(client, userId, folder.Id!, allFolders, ct);

            if (response.OdataNextLink is null) break;
            response = await client.Users[userId].MailFolders
                .WithUrl(response.OdataNextLink)
                .GetAsync(cancellationToken: ct);
        }

        return allFolders;
    }

    private async Task EnumerateChildFoldersAsync(
        GraphServiceClient client, string userId, string parentFolderId,
        List<MailFolder> accumulator, CancellationToken ct)
    {
        var response = await client.Users[userId].MailFolders[parentFolderId].ChildFolders
            .GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = 100;
                cfg.QueryParameters.Select = new[] { "id", "displayName", "parentFolderId", "totalItemCount", "childFolderCount" };
            }, ct);

        while (response?.Value is not null)
        {
            accumulator.AddRange(response.Value);
            foreach (var folder in response.Value.Where(f => (f.ChildFolderCount ?? 0) > 0))
                await EnumerateChildFoldersAsync(client, userId, folder.Id!, accumulator, ct);

            if (response.OdataNextLink is null) break;
            response = await client.Users[userId].MailFolders[parentFolderId].ChildFolders
                .WithUrl(response.OdataNextLink)
                .GetAsync(cancellationToken: ct);
        }
    }

    private async Task<int> CopyFolderMessagesAsync(
        GraphServiceClient sourceClient,
        GraphServiceClient targetClient,
        string sourceUserId,
        string targetUserId,
        string sourceFolderId,
        string targetFolderId,
        int pageSize,
        SemaphoreSlim throttle,
        CopyCounters counters,
        Func<Task> throttleCheck,
        CancellationToken ct)
    {
        var copied = 0;

        var response = await sourceClient.Users[sourceUserId]
            .MailFolders[sourceFolderId].Messages
            .GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = pageSize;
                cfg.QueryParameters.Select = new[]
                {
                    "id", "subject", "body", "from", "toRecipients", "ccRecipients",
                    "bccRecipients", "sentDateTime", "receivedDateTime", "importance",
                    "isRead", "internetMessageId", "categories", "flag",
                    "hasAttachments", "replyTo", "internetMessageHeaders",
                    "conversationId", "bodyPreview"
                };
                cfg.QueryParameters.Orderby = new[] { "receivedDateTime asc" };
            }, ct);

        while (response?.Value is not null)
        {
            foreach (var sourceMsg in response.Value)
            {
                ct.ThrowIfCancellationRequested();
                await throttleCheck();
                await throttle.WaitAsync(ct);

                try
                {
                    // Check for duplicate via internetMessageId (throttle-aware).
                    if (!string.IsNullOrEmpty(sourceMsg.InternetMessageId))
                    {
                        var existing = await ExecuteWithThrottleRetryAsync(
                            () => targetClient.Users[targetUserId]
                                .MailFolders[targetFolderId].Messages
                                .GetAsync(cfg =>
                                {
                                    cfg.QueryParameters.Filter =
                                        $"internetMessageId eq '{EscapeODataString(sourceMsg.InternetMessageId)}'";
                                    cfg.QueryParameters.Top = 1;
                                    cfg.QueryParameters.Select = new[] { "id" };
                                }, ct),
                            "duplicate check", ct);

                        if (existing?.Value?.Count > 0)
                        {
                            copied++;
                            continue;
                        }
                    }

                    // Create the message in the target folder. Attachments are copied
                    // separately afterwards — inline Attachments on the create call fail
                    // for payloads over ~3 MB and can't represent item attachments.
                    var targetMsg = BuildTargetMessage(sourceMsg);
                    var created = await ExecuteWithThrottleRetryAsync(
                        () => targetClient.Users[targetUserId]
                            .MailFolders[targetFolderId].Messages
                            .PostAsync(targetMsg, cancellationToken: ct),
                        "message create", ct);

                    if (sourceMsg.HasAttachments == true && created?.Id is not null)
                    {
                        await CopyAttachmentsAsync(
                            sourceClient, targetClient, sourceUserId, targetUserId,
                            sourceMsg.Id!, created.Id, counters, ct);
                    }

                    copied++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "GraphMailCopy: failed to copy message '{Subject}' — skipping.",
                        sourceMsg.Subject);
                    counters.RecordFailure($"'{sourceMsg.Subject}': {ex.Message}");
                }
                finally
                {
                    throttle.Release();
                }
            }

            if (response.OdataNextLink is null) break;
            response = await sourceClient.Users[sourceUserId]
                .MailFolders[sourceFolderId].Messages
                .WithUrl(response.OdataNextLink)
                .GetAsync(cancellationToken: ct);
        }

        return copied;
    }

    /// <summary>
    /// Runs a Graph call, waiting out 429/503 throttle responses up to 3 times before
    /// letting the exception surface. Non-throttle errors propagate immediately.
    /// </summary>
    private async Task<T> ExecuteWithThrottleRetryAsync<T>(
        Func<Task<T>> action, string operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (ApiException ex) when (
                (ex.ResponseStatusCode == 429 || ex.ResponseStatusCode == 503) && attempt <= 3)
            {
                _logger.LogWarning(
                    "GraphMailCopy: throttled on {Operation} (attempt {Attempt}/3) — waiting 60s.",
                    operation, attempt);
                await Task.Delay(ThrottleCooldownMs, ct);
            }
        }
    }

    // Attachments up to this size are posted inline; larger ones go through an
    // upload session (Graph rejects inline file attachments over ~3 MB).
    private const long SmallAttachmentMaxBytes = 3 * 1024 * 1024;
    // Upload-session chunks must be multiples of 320 KiB (except the final chunk).
    private const int UploadChunkSize = 10 * 320 * 1024;

    // Upload-session URLs are pre-authenticated; a plain shared HttpClient is correct here.
    private static readonly HttpClient AttachmentUploadClient = new();

    private async Task CopyAttachmentsAsync(
        GraphServiceClient sourceClient,
        GraphServiceClient targetClient,
        string sourceUserId,
        string targetUserId,
        string sourceMessageId,
        string targetMessageId,
        CopyCounters counters,
        CancellationToken ct)
    {
        var page = await sourceClient.Users[sourceUserId]
            .Messages[sourceMessageId].Attachments
            .GetAsync(cfg => { cfg.QueryParameters.Top = 20; }, ct);

        while (page?.Value is not null)
        {
            foreach (var att in page.Value)
            {
                ct.ThrowIfCancellationRequested();

                if (att is not FileAttachment file)
                {
                    // Item/reference attachments can't round-trip through the Graph
                    // create surface — count and continue rather than failing the message.
                    counters.AttachmentsSkipped++;
                    _logger.LogWarning(
                        "GraphMailCopy: skipping non-file attachment '{Name}' ({Type}) on message {MessageId}.",
                        att.Name, att.OdataType, sourceMessageId);
                    continue;
                }

                // The list call usually inlines contentBytes; re-fetch when it doesn't.
                var contentBytes = file.ContentBytes;
                if (contentBytes is null && file.Id is not null)
                {
                    var fetched = await ExecuteWithThrottleRetryAsync(
                        () => sourceClient.Users[sourceUserId]
                            .Messages[sourceMessageId].Attachments[file.Id]
                            .GetAsync(cancellationToken: ct),
                        "attachment fetch", ct);
                    contentBytes = (fetched as FileAttachment)?.ContentBytes;
                }

                if (contentBytes is null)
                {
                    counters.AttachmentsSkipped++;
                    _logger.LogWarning(
                        "GraphMailCopy: could not read content of attachment '{Name}' on message {MessageId} — skipping.",
                        file.Name, sourceMessageId);
                    continue;
                }

                if (contentBytes.LongLength <= SmallAttachmentMaxBytes)
                {
                    await ExecuteWithThrottleRetryAsync(
                        () => targetClient.Users[targetUserId]
                            .Messages[targetMessageId].Attachments
                            .PostAsync(new FileAttachment
                            {
                                Name = file.Name,
                                ContentType = file.ContentType,
                                ContentBytes = contentBytes,
                                IsInline = file.IsInline,
                                ContentId = file.ContentId,
                            }, cancellationToken: ct),
                        "attachment create", ct);
                }
                else
                {
                    await UploadLargeAttachmentAsync(
                        targetClient, targetUserId, targetMessageId, file, contentBytes, ct);
                }
            }

            if (page.OdataNextLink is null) break;
            page = await sourceClient.Users[sourceUserId]
                .Messages[sourceMessageId].Attachments
                .WithUrl(page.OdataNextLink)
                .GetAsync(cancellationToken: ct);
        }
    }

    private async Task UploadLargeAttachmentAsync(
        GraphServiceClient targetClient,
        string targetUserId,
        string targetMessageId,
        FileAttachment file,
        byte[] contentBytes,
        CancellationToken ct)
    {
        var session = await targetClient.Users[targetUserId]
            .Messages[targetMessageId].Attachments.CreateUploadSession
            .PostAsync(new Microsoft.Graph.Users.Item.Messages.Item.Attachments.CreateUploadSession.CreateUploadSessionPostRequestBody
            {
                AttachmentItem = new AttachmentItem
                {
                    AttachmentType = AttachmentType.File,
                    Name = file.Name,
                    Size = contentBytes.LongLength,
                    ContentType = file.ContentType,
                    IsInline = file.IsInline,
                },
            }, cancellationToken: ct);

        if (session?.UploadUrl is null)
            throw new InvalidOperationException(
                $"Upload session for attachment '{file.Name}' returned no upload URL.");

        var total = contentBytes.LongLength;
        for (long offset = 0; offset < total; offset += UploadChunkSize)
        {
            var length = (int)Math.Min(UploadChunkSize, total - offset);
            using var request = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl)
            {
                Content = new ByteArrayContent(contentBytes, (int)offset, length),
            };
            request.Content.Headers.ContentLength = length;
            request.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(offset, offset + length - 1, total);

            var response = await AttachmentUploadClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Chunk upload for attachment '{file.Name}' failed with HTTP {(int)response.StatusCode}: {body}");
            }
        }

        _logger.LogInformation(
            "GraphMailCopy: uploaded large attachment '{Name}' ({Size} bytes) via upload session.",
            file.Name, total);
    }

    private static Message BuildTargetMessage(Message source)
    {
        // sentDateTime/receivedDateTime/isRead are read-only on create — posting them
        // directly yields a Draft stamped with "now". The MAPI truth lives in extended
        // properties, which ARE writable on create:
        //   PR_MESSAGE_FLAGS   (Integer 0x0E07)  — clears MSGFLAG_UNSENT so it's not a draft
        //   PR_CLIENT_SUBMIT_TIME  (SystemTime 0x0039) — original sent time
        //   PR_MESSAGE_DELIVERY_TIME (SystemTime 0x0E06) — original received time
        var extendedProps = new List<SingleValueLegacyExtendedProperty>
        {
            new()
            {
                Id = "Integer 0x0E07",
                // MSGFLAG_READ (0x1) per source state; MSGFLAG_UNSENT (0x8) intentionally not set.
                Value = source.IsRead == true ? "1" : "0",
            },
        };
        if (source.SentDateTime is { } sent)
            extendedProps.Add(new SingleValueLegacyExtendedProperty
            {
                Id = "SystemTime 0x0039",
                Value = sent.UtcDateTime.ToString("o"),
            });
        if (source.ReceivedDateTime is { } received)
            extendedProps.Add(new SingleValueLegacyExtendedProperty
            {
                Id = "SystemTime 0x0E06",
                Value = received.UtcDateTime.ToString("o"),
            });

        return new Message
        {
            Subject = source.Subject,
            Body = source.Body,
            From = source.From,
            ToRecipients = source.ToRecipients,
            CcRecipients = source.CcRecipients,
            BccRecipients = source.BccRecipients,
            Importance = source.Importance,
            Categories = source.Categories,
            Flag = source.Flag,
            ReplyTo = source.ReplyTo,
            InternetMessageHeaders = source.InternetMessageHeaders,
            SingleValueExtendedProperties = extendedProps,
        };
    }

    private async Task<string> GetOrCreateFolderAsync(
        GraphServiceClient client, string userId, string? parentFolderId,
        string folderName, CancellationToken ct)
    {
        // Check if folder exists
        MailFolderCollectionResponse? existing;
        if (parentFolderId is null)
        {
            existing = await client.Users[userId].MailFolders
                .GetAsync(cfg =>
                {
                    cfg.QueryParameters.Filter = $"displayName eq '{EscapeODataString(folderName)}'";
                    cfg.QueryParameters.Top = 1;
                    cfg.QueryParameters.Select = new[] { "id" };
                }, ct);
        }
        else
        {
            existing = await client.Users[userId].MailFolders[parentFolderId].ChildFolders
                .GetAsync(cfg =>
                {
                    cfg.QueryParameters.Filter = $"displayName eq '{EscapeODataString(folderName)}'";
                    cfg.QueryParameters.Top = 1;
                    cfg.QueryParameters.Select = new[] { "id" };
                }, ct);
        }

        if (existing?.Value?.Count > 0)
            return existing.Value[0].Id!;

        // Create the folder
        var newFolder = new MailFolder { DisplayName = folderName };

        MailFolder? created;
        if (parentFolderId is null)
        {
            created = await client.Users[userId].MailFolders
                .PostAsync(newFolder, cancellationToken: ct);
        }
        else
        {
            created = await client.Users[userId].MailFolders[parentFolderId].ChildFolders
                .PostAsync(newFolder, cancellationToken: ct);
        }

        return created!.Id!;
    }

    private async Task<string?> GetWellKnownFolderIdAsync(
        GraphServiceClient client, string userId, string wellKnownName, CancellationToken ct)
    {
        try
        {
            var folder = await client.Users[userId].MailFolders[wellKnownName]
                .GetAsync(cfg =>
                {
                    cfg.QueryParameters.Select = new[] { "id" };
                }, ct);
            return folder?.Id;
        }
        catch
        {
            return null;
        }
    }

    private static string? MapToWellKnownFolder(string displayName)
    {
        return displayName.ToLowerInvariant() switch
        {
            "inbox"         => "inbox",
            "sent items"    => "sentitems",
            "drafts"        => "drafts",
            "deleted items" => "deleteditems",
            "junk email"    => "junkemail",
            "archive"       => "archive",
            _               => null,
        };
    }

    private static bool IsSystemFolder(string displayName)
    {
        var lower = displayName.ToLowerInvariant();
        return lower is "conversation history" or "sync issues" or "conflicts"
            or "local failures" or "server failures";
    }

    private static string EscapeODataString(string value)
        => value.Replace("'", "''");

    private async Task ThrottleCheckAsync(int maxRequests, CancellationToken ct)
    {
        _requestCount++;
        if (DateTime.UtcNow - _requestWindowStart > TimeSpan.FromMinutes(10))
        {
            _requestCount = 1;
            _requestWindowStart = DateTime.UtcNow;
            return;
        }

        if (_requestCount >= maxRequests)
        {
            _logger.LogInformation("GraphMailCopy: approaching throttle limit ({Count} requests) — cooling down 60s.",
                _requestCount);
            await Task.Delay(ThrottleCooldownMs, ct);
            _requestCount = 0;
            _requestWindowStart = DateTime.UtcNow;
        }
    }
}
