using Azure.Core;

namespace MigrationPlatform.Api.Services.Exo;

/// <summary>
/// HTTP client for the Exchange Online REST API (adminapi/beta).
/// Requires an app registration with the <c>Exchange.ManageAsApp</c> application permission
/// and Exchange Online admin consent. Credentials are supplied per-call as a
/// <see cref="TokenCredential"/> so the same factory can service multiple tenants.
/// </summary>
public interface IExoRestClient
{
    /// <summary>
    /// Returns mailbox size, item count, and last logon time for a single mailbox.
    /// Returns <see langword="null"/> if the mailbox does not exist (404).
    /// </summary>
    Task<ExoMailboxStats?> GetMailboxStatisticsAsync(
        string aadTenantId, string upn, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Returns archive presence and size for a single mailbox.
    /// </summary>
    Task<ExoArchiveInfo> GetMailboxArchiveInfoAsync(
        string aadTenantId, string upn, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Searches for a cross-tenant migration endpoint in the source tenant.
    /// When <paramref name="remoteTenant"/> is supplied only endpoints whose
    /// <c>RemoteTenant</c> matches are returned — required in multi-tenant-pair setups
    /// where several <c>ExchangeRemoteMove</c> endpoints coexist.
    /// Returns the endpoint <c>Identity</c> string, or <see langword="null"/> if none exists
    /// (including when the EXO organization has never been configured for cross-tenant migration).
    /// </summary>
    Task<string?> FindCrossTenantMigrationEndpointAsync(
        string aadTenantId, TokenCredential credential, CancellationToken ct,
        string? remoteTenant = null);

    /// <summary>
    /// Ensures an organization relationship exists in <paramref name="aadTenantId"/> that
    /// targets <paramref name="partnerDomain"/> with the specified
    /// <paramref name="mailboxMoveCapability"/> (<c>"Inbound"</c>/<c>"Outbound"</c>/<c>"RemoteInbound"</c>/<c>"RemoteOutbound"</c>).
    /// When <paramref name="oauthApplicationId"/> and/or <paramref name="mailboxMovePublishedScopes"/> are supplied,
    /// they are written to the relationship — required on the source's <c>RemoteOutbound</c> rel for cross-tenant
    /// mailbox moves to authenticate. If the relationship already exists, missing/stale auth fields are patched
    /// via <c>Set-OrganizationRelationship</c>.
    /// <paramref name="partnerTenantId"/> is the partner's Entra tenant GUID — Microsoft's current
    /// cross-tenant mailbox doc requires the tenant GUID (not the domain) in <c>DomainNames</c>;
    /// both are stamped so existing domain-based matching keeps working.
    /// Returns <see langword="true"/> if a new relationship was created, <see langword="false"/> if one already existed.
    /// </summary>
    Task<bool> EnsureOrganizationRelationshipAsync(
        string aadTenantId,
        string partnerDomain,
        string name,
        string mailboxMoveCapability,
        TokenCredential credential,
        CancellationToken ct,
        string? oauthApplicationId = null,
        string? mailboxMovePublishedScopes = null,
        string? partnerTenantId = null);

    /// <summary>
    /// Ensures a cross-tenant migration endpoint exists in the target tenant.
    /// Creates one targeting <paramref name="targetTenantDomain"/> (the source's
    /// <c>*.onmicrosoft.com</c> domain) if none exists.
    /// <paramref name="applicationId"/> must match the <c>OAuthApplicationId</c> stamped on
    /// both org relationships — omitting it causes EXO to create the endpoint without auth
    /// credentials and the migration batch will fail with a 400 from MRS.
    /// <paramref name="clientSecret"/> is the migration app's client secret; current MS
    /// infrastructure requires the endpoint to be created with <c>-Credentials</c>
    /// (a PSCredential of AppId + secret) — creating without it yields an endpoint that
    /// cannot authenticate (see CLAUDE.md cross-tenant prereq #7). When the endpoint must
    /// be created and no secret is supplied, this method throws with an actionable message
    /// instead of creating a broken endpoint.
    /// Returns <c>(identity, wasCreated)</c> — <c>wasCreated</c> is <see langword="true"/>
    /// when a new endpoint was created, <see langword="false"/> when one already existed.
    /// </summary>
    Task<(string Identity, bool WasCreated)> EnsureMigrationEndpointAsync(
        string aadTenantId,
        string targetTenantDomain,
        TokenCredential credential,
        CancellationToken ct,
        string? applicationId = null,
        string? clientSecret = null);

    /// <summary>
    /// Creates a new cross-tenant mailbox migration batch in EXO.
    /// The batch is created in Stopped/Suspended state — EXO begins syncing only after
    /// the batch is explicitly started or completed.
    /// </summary>
    Task<ExoBatchCreationResult> CreateMigrationBatchAsync(
        string aadTenantId,
        string batchName,
        string targetDeliveryDomain,
        string endpointIdentity,
        IEnumerable<string> migrationIdentities,
        TokenCredential credential,
        CancellationToken ct);

    /// <summary>
    /// Returns the current aggregate status of an EXO migration batch.
    /// Returns <see langword="null"/> if the batch does not exist (404).
    /// </summary>
    Task<ExoBatchStatus?> GetMigrationBatchAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Removes an existing migration batch from EXO. Used to clean up stale batches
    /// before retrying a batch with the same name.
    /// </summary>
    Task RemoveMigrationBatchAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Removes any in-flight or stuck <c>MoveRequest</c> for the given mailbox identity
    /// (target UPN). No-op when no MoveRequest exists. Used to clear MRS state before
    /// retrying a cross-tenant move — <c>Remove-MigrationBatch</c> alone leaves orphan
    /// MoveRequests that block <c>New-MoveRequest</c>/the next batch.
    /// </summary>
    Task RemoveMoveRequestAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Removes a target <c>MailUser</c> entirely (soft-delete) — used to undo a partial
    /// provisioning before retry. No-op when the MailUser does not exist.
    /// </summary>
    Task RemoveMailUserAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Returns identities of soft-deleted MailUsers in <paramref name="aadTenantId"/> whose
    /// <c>ExternalEmailAddress</c> matches one of <paramref name="externalEmailAddresses"/>
    /// (case-insensitive, with or without an <c>SMTP:</c> prefix). Empty list when no
    /// matches exist. Used to identify the stale target stubs that block re-provisioning
    /// after a failed migration attempt (the documented cross-tenant migration blocker).
    /// </summary>
    Task<IReadOnlyList<string>> GetSoftDeletedMailUsersByExternalEmailAsync(
        string aadTenantId,
        IEnumerable<string> externalEmailAddresses,
        TokenCredential credential,
        CancellationToken ct);

    /// <summary>
    /// Permanently purges a soft-deleted <c>MailUser</c> (<c>Remove-MailUser -PermanentlyDelete</c>).
    /// This frees the SMTP/proxy addresses for reuse on a retry. No-op on missing object.
    /// </summary>
    Task PurgeSoftDeletedMailUserAsync(
        string aadTenantId, string identity, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Signals EXO to begin the final cutover phase for a batch in Syncing state.
    /// </summary>
    Task CompleteMigrationBatchAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Returns the per-user migration status for all users within a batch.
    /// </summary>
    Task<IReadOnlyList<ExoMigrationUser>> GetMigrationUsersAsync(
        string aadTenantId, string exoBatchId, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Enables a user as a <c>MailUser</c> in Exchange Online with the specified
    /// <paramref name="externalEmailAddress"/>. This is required for cross-tenant
    /// mailbox migration — the target user must have <c>RecipientTypeDetails:MailUser</c>
    /// with <c>ExternalEmailAddress</c> pointing to the source mailbox.
    /// Uses the <c>Enable-MailUser</c> cmdlet via InvokeCommand.
    /// </summary>
    Task EnableMailUserAsync(
        string aadTenantId,
        string identity,
        string externalEmailAddress,
        TokenCredential credential,
        CancellationToken ct);

    /// <summary>
    /// Sets the primary SMTP address on a mailbox and optionally adds the domain
    /// as an email alias. Used during domain cutover to restore the user's original
    /// email address after the domain has been verified in the target tenant.
    /// Uses the <c>Set-Mailbox</c> cmdlet via InvokeCommand.
    /// </summary>
    Task SetMailboxPrimarySmtpAsync(
        string aadTenantId,
        string identity,
        string primarySmtpAddress,
        TokenCredential credential,
        CancellationToken ct);

    /// <summary>
    /// Captures the attributes of a source mailbox needed to provision a matching
    /// target MailUser for native cross-tenant MRS. Returns <see langword="null"/>
    /// if the mailbox does not exist on source.
    /// </summary>
    Task<ExoMailboxAttributes?> GetMailboxAttributesAsync(
        string aadTenantId, string upn, TokenCredential credential, CancellationToken ct);

    /// <summary>
    /// Provisions a MailUser on the target tenant suitable for an inbound cross-tenant
    /// mailbox move: creates the recipient with target-routing UPN and source-routing
    /// <c>ExternalEmailAddress</c>, then stamps <c>ExchangeGuid</c> + the source's
    /// <c>LegacyExchangeDN</c> as an <c>x500:</c> proxy, plus a secondary <c>smtp:</c>
    /// proxy at <paramref name="targetRoutingDomain"/> (MRS refuses the move when the
    /// target stub lacks an address at the batch's TargetDeliveryDomain). Idempotent —
    /// if a MailUser already exists at <paramref name="targetUpn"/> it is patched in place.
    /// </summary>
    Task EnsureTargetMailUserAsync(
        string aadTenantId,
        string targetUpn,
        ExoMailboxAttributes sourceAttributes,
        string targetRoutingDomain,
        TokenCredential credential,
        CancellationToken ct);

    /// <summary>
    /// Ensures a mail-enabled distribution group of the given name exists in
    /// <paramref name="aadTenantId"/> with <c>-Type Security</c>. The group is used as
    /// the source's <c>MailboxMovePublishedScopes</c> on the cross-tenant org relationship —
    /// only members are eligible to migrate. Returns the group's identity (Name).
    /// </summary>
    Task<string> EnsureScopeDistributionGroupAsync(
        string aadTenantId,
        string groupName,
        string primarySmtpAddress,
        TokenCredential credential,
        CancellationToken ct);

    /// <summary>
    /// Adds a member to a distribution group. Idempotent: if the user is already a
    /// member the call returns without error.
    /// </summary>
    Task AddDistributionGroupMemberAsync(
        string aadTenantId,
        string groupIdentity,
        string memberUpn,
        TokenCredential credential,
        CancellationToken ct);

    /// <summary>
    /// Invokes an EXO cmdlet via the InvokeCommand REST endpoint and returns the full,
    /// raw response — HTTP status, headers, body bytes (hex preview), parsed results
    /// when JSON, and token claims used for auth. Designed for the diagnostic controller:
    /// callers want to inspect every byte of the failure case, not just get a parsed object
    /// or an exception. Never throws on cmdlet errors — failures are reported in the result.
    /// Throws only on transport-level errors (DNS, TLS, etc.) and token acquisition.
    /// </summary>
    Task<ExoRawInvokeResult> InvokeCommandRawAsync(
        string aadTenantId,
        string cmdletName,
        Dictionary<string, object> parameters,
        TokenCredential credential,
        CancellationToken ct);
}
