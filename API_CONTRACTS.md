# API Contracts — M365 Tenant Migration Platform

Base URL: `http://localhost:5000/api`

---

## Tenants

| Method | Path | Description |
|--------|------|-------------|
| GET    | /tenants | List all tenant connections |
| POST   | /tenants | Register a new tenant |
| GET    | /tenants/:id | Get tenant details |
| PUT    | /tenants/:id | Update tenant |
| DELETE | /tenants/:id | Remove tenant |
| POST   | /tenants/:id/verify | Test app connection |

### Tenant Object
```json
{
  "id": "uuid",
  "displayName": "Contoso Ltd",
  "tenantId": "azure-tenant-id",
  "role": "source|target",
  "appClientId": "app-registration-client-id",
  "authMethod": "certificate|secret",
  "adminConsentGranted": true,
  "connectionStatus": "connected|pending|failed|unverified",
  "lastVerifiedAt": "ISO8601",
  "createdAt": "ISO8601"
}
```

---

## Migration Projects

| Method | Path | Description |
|--------|------|-------------|
| GET    | /projects | List projects |
| POST   | /projects | Create project |
| GET    | /projects/:id | Get project |
| PUT    | /projects/:id | Update project |

### Project Object
```json
{
  "id": "uuid",
  "name": "Contoso → Fabrikam Migration",
  "sourceTenantId": "uuid",
  "targetTenantId": "uuid",
  "status": "draft|active|completed|paused",
  "createdAt": "ISO8601"
}
```

---

## Scans

| Method | Path | Description |
|--------|------|-------------|
| POST   | /scans | Start a new scan |
| GET    | /scans | List scans |
| GET    | /scans/:id | Get scan + summary |
| GET    | /scans/:id/users | Scanned users |
| GET    | /scans/:id/groups | Scanned groups |
| GET    | /scans/:id/mailboxes | Scanned mailboxes |
| GET    | /scans/:id/sites | SharePoint sites |
| GET    | /scans/:id/onedrive | OneDrive accounts |
| GET    | /scans/:id/domains | Accepted domains |
| GET    | /scans/:id/issues | Blockers & warnings |

### Scan Object
```json
{
  "id": "uuid",
  "tenantId": "uuid",
  "projectId": "uuid",
  "scanType": "full|users|mailboxes|sharepoint|onedrive|domains",
  "status": "queued|running|completed|failed",
  "progress": 65,
  "startedAt": "ISO8601",
  "completedAt": "ISO8601",
  "summary": {
    "userCount": 250,
    "groupCount": 45,
    "mailboxCount": 230,
    "mailboxTotalSizeGb": 180.5,
    "siteCount": 38,
    "oneDriveCount": 228,
    "domainCount": 3,
    "blockerCount": 2,
    "warningCount": 12,
    "readinessScore": 87
  }
}
```

### Scanned User
```json
{
  "id": "uuid",
  "sourceObjectId": "graph-object-id",
  "displayName": "Jane Doe",
  "upn": "jane@contoso.com",
  "accountEnabled": true,
  "licenses": ["Microsoft 365 E3"],
  "hasMailbox": true,
  "mailboxSizeGb": 4.2,
  "mailboxType": "UserMailbox",
  "oneDriveSizeGb": 12.1,
  "mfaEnabled": true
}
```

### Scanned Site
```json
{
  "id": "uuid",
  "siteUrl": "https://contoso.sharepoint.com/sites/HR",
  "title": "HR Portal",
  "template": "TeamSite",
  "storageUsedGb": 8.3,
  "storageQuotaGb": 25.0,
  "owners": ["admin@contoso.com"],
  "lastActivityDate": "ISO8601",
  "hasUniquePermissions": true,
  "subsiteCount": 2
}
```

### Issue / Blocker
```json
{
  "id": "uuid",
  "severity": "blocker|warning|info",
  "category": "identity|mailbox|sharepoint|onedrive|domain|license|permissions",
  "code": "DOMAIN_NOT_IN_TARGET",
  "title": "Domain not present in target tenant",
  "description": "The domain contoso.com is used by 200 users but does not exist in the target tenant.",
  "affectedObjectCount": 200,
  "remediationSteps": ["Add and verify contoso.com in the target tenant admin center."]
}
```

---

## Identity Maps

| Method | Path | Description |
|--------|------|-------------|
| GET    | /projects/:id/identity-maps | List mappings |
| POST   | /projects/:id/identity-maps/auto-map | Auto-match by UPN/display name |
| PUT    | /projects/:id/identity-maps/:mapId | Update a mapping |
| POST   | /projects/:id/identity-maps/import | Import CSV |
| GET    | /projects/:id/identity-maps/export | Export CSV |

---

## Jobs

| Method | Path | Description |
|--------|------|-------------|
| GET    | /jobs | List all jobs |
| GET    | /jobs/:id | Get job details |
| POST   | /jobs/:id/retry | Retry failed job |
| POST   | /jobs/:id/cancel | Cancel job |

### Job Object
```json
{
  "id": "uuid",
  "projectId": "uuid",
  "type": "scan|identity_map|mailbox_migrate|sharepoint_migrate|onedrive_migrate",
  "status": "queued|running|completed|failed|cancelled",
  "progress": 45,
  "createdAt": "ISO8601",
  "startedAt": "ISO8601",
  "completedAt": "ISO8601",
  "errorMessage": null,
  "itemsTotal": 100,
  "itemsProcessed": 45,
  "itemsFailed": 2
}
```

---

## Audit Events

| Method | Path | Description |
|--------|------|-------------|
| GET    | /audit | List audit events (paginated) |

### AuditEvent Object
```json
{
  "id": "uuid",
  "timestamp": "ISO8601",
  "actor": "admin@platform.com",
  "action": "SCAN_STARTED",
  "resource": "scans/uuid",
  "projectId": "uuid",
  "outcome": "success|failure",
  "details": {}
}
```
