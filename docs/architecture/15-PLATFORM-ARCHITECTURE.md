# 15 — Platform Architecture: Kubernetes, Web GUI & Enterprise Auth

> Evolution plan for transitioning HomeManagement from a single-machine desktop application
> to a containerized, multi-user platform with web-based access, enterprise authentication,
> and an external SQL Server data tier.
>
> **Revision 1 — 2026-03-18:** Initial platform architecture.

---

## 1. Executive Summary

The current HomeManagement system runs as a desktop application (Avalonia GUI) with an embedded
SQLite database on a single operator workstation. This document defines the architecture for
**Phase 2**: a Kubernetes-hosted platform that:

- Moves the **Command Broker** and **Orchestrator** into containerized microservices
- Replaces SQLite with **Microsoft SQL Server** for multi-user concurrent access
- Adds a **web-based GUI** (ASP.NET Core + Blazor Server) accessible from any browser
- Introduces **enterprise authentication** (Active Directory, SAML 2.0, OAuth 2.0 / OIDC)
- Preserves the existing agent protocol (gRPC bidirectional streaming on port 9444)
- Maintains backward compatibility with the desktop GUI during the transition period

### Design Principles

| Principle | Rationale |
|---|---|
| **Interface preservation** | All `HomeManagement.Abstractions` interfaces remain unchanged — new deployment, same contracts |
| **Strangler fig migration** | Each subsystem migrates independently; desktop and web GUIs can coexist during rollout |
| **Zero-trust networking** | All inter-service communication is mTLS; no implicit trust between pods |
| **12-Factor compliance** | Config via environment/secrets, stateless services, disposable containers |
| **Auth as a first-class boundary** | Every API request authenticated and authorized before reaching domain logic |

---

## 2. Target Architecture Diagram

```
┌──────────────────────────────────────────────────────────────────────────────────┐
│                              KUBERNETES CLUSTER                                   │
│                                                                                   │
│  ┌─────────────────────────────────────────────────────────────────────────────┐  │
│  │  NAMESPACE: homemanagement                                                  │  │
│  │                                                                             │  │
│  │  ┌───────────────────────────────────┐   ┌───────────────────────────────┐  │  │
│  │  │       WEB GUI POD (Blazor)        │   │    API GATEWAY POD (YARP)     │  │  │
│  │  │  ┌─────────────────────────────┐  │   │  ┌─────────────────────────┐  │  │  │
│  │  │  │  ASP.NET Core 8             │  │   │  │  YARP Reverse Proxy     │  │  │  │
│  │  │  │  Blazor Server              │  │   │  │  Rate Limiting          │  │  │  │
│  │  │  │  SignalR (real-time)        │  │   │  │  Auth Middleware         │  │  │  │
│  │  │  │  Tailwind CSS              │  │   │  │  Request Routing         │  │  │  │
│  │  │  └─────────────────────────────┘  │   │  └─────────────────────────┘  │  │  │
│  │  └───────────────┬───────────────────┘   └──────────────┬────────────────┘  │  │
│  │                  │                                      │                   │  │
│  │        ──────────┴──────────────────────────────────────┴────────────       │  │
│  │                          INTERNAL CLUSTER NETWORK                            │  │
│  │        ──────────┬──────────────┬──────────────┬───────────────┬────────    │  │
│  │                  │              │              │               │             │  │
│  │  ┌───────────────┴──────┐  ┌───┴───────────┐  ┌──┴────────────┴──────────┐  │  │
│  │  │  BROKER SERVICE POD  │  │  AUTH SERVICE │  │  AGENT GATEWAY POD       │  │  │
│  │  │  ┌────────────────┐  │  │  POD          │  │  ┌────────────────────┐  │  │  │
│  │  │  │ CommandBroker  │  │  │  ┌──────────┐ │  │  │  gRPC Server       │  │  │  │
│  │  │  │ JobScheduler   │  │  │  │ Identity │ │  │  │  :9444 (mTLS)      │  │  │  │
│  │  │  │ Orchestrator   │  │  │  │ Server   │ │  │  │  AgentGateway      │  │  │  │
│  │  │  │ PatchService   │  │  │  │          │ │  │  │  HeartbeatTracker  │  │  │  │
│  │  │  │ ServiceCtrl    │  │  │  │ AD/SAML/ │ │  │  └────────────────────┘  │  │  │
│  │  │  │ AuditLogger    │  │  │  │ OAuth    │ │  │                          │  │  │
│  │  │  └────────────────┘  │  │  └──────────┘ │  └──────────────────────────┘  │  │
│  │  └──────────────────────┘  └───────────────┘                                │  │
│  │                                                                             │  │
│  │  ┌─────────────────────────────────────────────────────────────────────────┐│  │
│  │  │  INFRASTRUCTURE SERVICES                                                ││  │
│  │  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌────────────┐  ││  │
│  │  │  │ cert-manager │  │ Seq / Loki   │  │ Prometheus   │  │ Vault      │  ││  │
│  │  │  │ (TLS certs)  │  │ (central log)│  │ (metrics)    │  │ (secrets)  │  ││  │
│  │  │  └──────────────┘  └──────────────┘  └──────────────┘  └────────────┘  ││  │
│  │  └─────────────────────────────────────────────────────────────────────────┘│  │
│  └─────────────────────────────────────────────────────────────────────────────┘  │
│                                                                                   │
│  ┌─────────────────────────────┐    ┌──────────────────────────────────────────┐  │
│  │  INGRESS CONTROLLER         │    │  PERSISTENT VOLUMES                      │  │
│  │  (NGINX / Traefik)          │    │  ┌────────────────┐  ┌────────────────┐  │  │
│  │  TLS termination            │    │  │ vault.enc      │  │ agent certs    │  │  │
│  │  → Web GUI (:443)           │    │  │ (PVC)          │  │ (PVC)          │  │  │
│  │  → API Gateway (:443/api)   │    │  └────────────────┘  └────────────────┘  │  │
│  │  → Agent Gateway (:9444)    │    └──────────────────────────────────────────┘  │
│  └─────────────────────────────┘                                                  │
└──────────────────────────────────────────────────────────────────────────────────┘
                    │                                    │
                    │                                    │
        ┌───────────┴───────────────┐      ┌─────────────┴──────────────────┐
        │  EXTERNAL: SQL Server     │      │  MANAGED MACHINES              │
        │  ┌─────────────────────┐  │      │  ┌────────────┐ ┌───────────┐ │
        │  │ homemanagement_db   │  │      │  │ Linux      │ │ Windows   │ │
        │  │  ├─ Machines        │  │      │  │ Agents     │ │ Agents    │ │
        │  │  ├─ Jobs            │  │      │  │ :9444      │ │ :9444     │ │
        │  │  ├─ PatchHistory    │  │      │  └────────────┘ └───────────┘ │
        │  │  ├─ ServiceSnap     │  │      │                                │
        │  │  ├─ AuditEvents     │  │      │  ┌────────────┐ ┌───────────┐ │
        │  │  ├─ Users/Roles     │  │      │  │ SSH hosts  │ │ WinRM     │ │
        │  │  └─ Sessions        │  │      │  │ (agentless)│ │ (agentless│ │
        │  └─────────────────────┘  │      │  └────────────┘ └───────────┘ │
        └───────────────────────────┘      └────────────────────────────────┘
```

---

## 3. Service Decomposition

The monolithic desktop process is decomposed into four deployable services plus the
existing agent binary. Each service is a separate Kubernetes Deployment.

### 3.1 Service Inventory

| Service | Image | Replicas | Ports | State | Scaling Strategy |
|---|---|---|---|---|---|
| **Web GUI** | `hm-web:tag` | 2+ | 8080 (HTTP) | Stateless (session via SQL / Redis) | HPA on CPU/memory |
| **API Gateway** | `hm-gateway:tag` | 2+ | 8081 (HTTP) | Stateless | HPA on request rate |
| **Broker Service** | `hm-broker:tag` | 1 (active) | 8082 (HTTP), 9090 (metrics) | Stateful (in-memory queue) | Leader election for HA |
| **Auth Service** | `hm-auth:tag` | 2+ | 8083 (HTTP) | Stateless | HPA on auth request rate |
| **Agent Gateway** | `hm-agent-gw:tag` | 1 (active) | 9444 (gRPC) | Stateful (agent connections) | Leader election for HA |

### 3.2 Service Responsibilities

```
┌─────────────────────────────────────────────────────────────────────────┐
│  hm-web (Blazor Server)                                                 │
│  ├─ Server-side Blazor rendering (SignalR circuits)                     │
│  ├─ Real-time dashboard via SignalR push                                │
│  ├─ Calls Broker API for all domain operations                         │
│  ├─ Receives push events from Broker via SignalR backplane              │
│  └─ Delegates authentication to Auth Service                            │
├─────────────────────────────────────────────────────────────────────────┤
│  hm-gateway (YARP Reverse Proxy)                                        │
│  ├─ Routes /api/* → Broker, /auth/* → Auth Service                      │
│  ├─ Rate limiting (per-user, per-IP)                                    │
│  ├─ Request/response logging                                            │
│  ├─ CORS policy enforcement                                             │
│  └─ JWT validation middleware (delegates to Auth Service for issuance)   │
├─────────────────────────────────────────────────────────────────────────┤
│  hm-broker (Domain Engine)                                              │
│  ├─ Hosts ALL domain service implementations:                           │
│  │   ICommandBroker, IJobScheduler, IPatchService, IServiceController,  │
│  │   IInventoryService, IAuditLogger, ICredentialVault                  │
│  ├─ Exposes REST API for domain operations                              │
│  ├─ Manages CommandBrokerService (Channel<T> queue → execution)         │
│  ├─ Runs Quartz.NET scheduler for cron jobs                             │
│  ├─ Publishes events to SignalR backplane (Redis or SQL Server)         │
│  └─ Connects to SQL Server via EF Core                                  │
├─────────────────────────────────────────────────────────────────────────┤
│  hm-auth (Identity Service)                                             │
│  ├─ Issues JWT access + refresh tokens                                  │
│  ├─ Active Directory (LDAP / Kerberos) authentication                   │
│  ├─ SAML 2.0 SP (external IdP integration)                              │
│  ├─ OAuth 2.0 / OpenID Connect provider                                 │
│  ├─ Role-based access control (RBAC) evaluation                         │
│  ├─ Session management and token revocation                             │
│  └─ User/role provisioning API (admin-only)                             │
├─────────────────────────────────────────────────────────────────────────┤
│  hm-agent-gw (Agent Communication Hub)                                  │
│  ├─ gRPC bidirectional streaming server (:9444, mTLS)                   │
│  ├─ Agent registration, heartbeat tracking, connection management       │
│  ├─ Routes commands from Broker → Agent via gRPC stream                 │
│  ├─ Returns results from Agent → Broker via internal API                │
│  └─ Publishes agent connection events to SignalR backplane              │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Authentication & Authorization Subsystem

### 4.1 Authentication Flow Overview

```
                         ┌──────────────────┐
                         │   User Browser   │
                         └────────┬─────────┘
                                  │
                          HTTPS :443 (Ingress)
                                  │
                    ┌─────────────┴──────────────┐
                    ▼                             ▼
           ┌──────────────┐              ┌──────────────┐
           │  hm-web      │              │  hm-gateway  │
           │  (Blazor)    │              │  (YARP)      │
           └──────┬───────┘              └──────┬───────┘
                  │                             │
                  │    /auth/login              │  Authorization: Bearer <JWT>
                  │    /auth/saml/callback      │  → validate signature + claims
                  │    /auth/oauth/callback     │
                  ▼                             │
           ┌──────────────┐                     │
           │  hm-auth     │◄────────────────────┘
           │              │   token validation
           │  ┌────────┐  │
           │  │ AD/LDAP │  │
           │  ├────────┤  │
           │  │ SAML SP │  │
           │  ├────────┤  │
           │  │ OAuth   │  │
           │  │ /OIDC   │  │
           │  └────────┘  │
           └──────┬───────┘
                  │
                  ▼
           ┌──────────────┐
           │  SQL Server   │
           │  ├─ Users     │
           │  ├─ Roles     │
           │  ├─ Sessions  │
           │  └─ AuditLog  │
           └──────────────┘
```

### 4.2 Authentication Providers

| Provider | Protocol | Use Case | Token Type |
|---|---|---|---|
| **Active Directory** | LDAP bind / Kerberos | Corporate on-prem environments | JWT (issued by hm-auth) |
| **SAML 2.0** | SAML SP ↔ IdP | Federated enterprise SSO (Okta, Azure AD, ADFS) | JWT (after SAML assertion validation) |
| **OAuth 2.0 / OIDC** | Authorization Code + PKCE | Cloud-native IdPs (Azure AD, Google, GitHub) | JWT (from IdP or re-issued by hm-auth) |
| **Local** | Username/password (Argon2id) | Standalone / dev environments | JWT (issued by hm-auth) |

All providers produce the same JWT format after successful authentication. Downstream
services only validate JWTs — they never interact with IdPs directly.

### 4.3 JWT Token Structure

```json
{
  "iss": "hm-auth",
  "sub": "user-guid",
  "aud": "homemanagement",
  "exp": 1711000000,
  "iat": 1710996400,
  "jti": "unique-token-id",
  "name": "Jane Operator",
  "email": "jane@example.com",
  "roles": ["admin", "operator"],
  "permissions": [
    "machines:read", "machines:write",
    "patches:read", "patches:apply",
    "services:read", "services:control",
    "jobs:read", "jobs:submit", "jobs:cancel",
    "credentials:read", "credentials:write",
    "audit:read", "audit:export",
    "admin:users", "admin:settings"
  ],
  "auth_method": "active_directory",
  "tenant_id": "default"
}
```

### 4.4 Role-Based Access Control (RBAC)

| Role | Permissions | Description |
|---|---|---|
| **Viewer** | `*:read` | Read-only access to all pages |
| **Operator** | `*:read`, `patches:apply`, `services:control`, `jobs:submit`, `jobs:cancel` | Day-to-day operations |
| **Admin** | All permissions | Full access including user management and credential vault |
| **Auditor** | `audit:read`, `audit:export`, `machines:read`, `jobs:read` | Audit and compliance review |

Custom roles can be defined by combining granular permissions.

### 4.5 Active Directory Integration

```
┌────────────┐     ┌──────────────┐     ┌──────────────┐
│ User login │────→│  hm-auth     │────→│  AD / LDAP   │
│ (user/pass)│     │              │     │  Server      │
└────────────┘     │  1. LDAP     │     │              │
                   │     Bind     │     │  OU=Users,   │
                   │  2. Search   │     │  DC=corp,    │
                   │     groups   │     │  DC=local    │
                   │  3. Map AD   │     └──────────────┘
                   │     groups → │
                   │     HM roles │
                   │  4. Issue    │
                   │     JWT      │
                   └──────────────┘

AD Group Mapping (configurable):
  CN=HM-Admins    → Role: Admin
  CN=HM-Operators → Role: Operator
  CN=HM-Viewers   → Role: Viewer
  CN=HM-Auditors  → Role: Auditor
```

Configuration:

```json
{
  "Auth": {
    "ActiveDirectory": {
      "Enabled": true,
      "Domain": "corp.local",
      "LdapServer": "ldap://dc01.corp.local:389",
      "UseLdaps": true,
      "LdapsPort": 636,
      "SearchBase": "OU=Users,DC=corp,DC=local",
      "GroupSearchBase": "OU=Groups,DC=corp,DC=local",
      "ServiceAccountDn": "CN=hm-svc,OU=ServiceAccounts,DC=corp,DC=local",
      "GroupRoleMapping": {
        "CN=HM-Admins,OU=Groups,DC=corp,DC=local": "Admin",
        "CN=HM-Operators,OU=Groups,DC=corp,DC=local": "Operator",
        "CN=HM-Viewers,OU=Groups,DC=corp,DC=local": "Viewer",
        "CN=HM-Auditors,OU=Groups,DC=corp,DC=local": "Auditor"
      }
    }
  }
}
```

### 4.6 SAML 2.0 Integration

```
┌────────────┐     ┌──────────────┐     ┌──────────────┐
│ User login │────→│  hm-auth     │────→│ External IdP │
│ (redirect) │     │  (SAML SP)   │     │ (Okta, ADFS, │
└────────────┘     │              │     │  Azure AD)   │
      ▲            │  1. Generate │     └──────┬───────┘
      │            │     AuthnReq │            │
      │            │  2. Redirect │            │
      │            │     → IdP    │────────────┘
      │            │              │
      │            │  3. Receive  │
      └────────────│     SAMLResp │
                   │  4. Validate │
                   │     signature│
                   │  5. Extract  │
                   │     attributes│
                   │  6. Map → JWT│
                   └──────────────┘

SAML Attribute Mapping:
  urn:oid:0.9.2342.19200300.100.1.3  → email
  urn:oid:2.5.4.42                    → givenName
  urn:oid:2.5.4.4                     → surname
  urn:oid:1.2.840.113556.1.2.102      → memberOf (groups → roles)
```

Configuration:

```json
{
  "Auth": {
    "Saml": {
      "Enabled": true,
      "EntityId": "https://hm.corp.local/saml/metadata",
      "AssertionConsumerServiceUrl": "https://hm.corp.local/auth/saml/callback",
      "IdpMetadataUrl": "https://idp.example.com/metadata.xml",
      "IdpEntityId": "https://idp.example.com",
      "SigningCertPath": "/certs/saml-signing.pfx",
      "RequireSignedAssertions": true,
      "AttributeRoleMapping": {
        "HM-Admin": "Admin",
        "HM-Operator": "Operator"
      }
    }
  }
}
```

### 4.7 OAuth 2.0 / OpenID Connect Integration

```
┌────────────┐     ┌──────────────┐     ┌──────────────┐
│ User login │────→│  hm-auth     │────→│ OAuth/OIDC   │
│ (redirect) │     │              │     │ Provider     │
└────────────┘     │  1. AuthZ    │     │ (Azure AD,   │
      ▲            │     Code +   │     │  Google,     │
      │            │     PKCE     │     │  Keycloak)   │
      │            │  2. Redirect │     └──────┬───────┘
      │            │     → IdP    │            │
      │            │              │            │
      │            │  3. Callback │            │
      └────────────│     with code│◄───────────┘
                   │  4. Exchange │
                   │     code →   │
                   │     tokens   │
                   │  5. Validate │
                   │     id_token │
                   │  6. Map → JWT│
                   └──────────────┘
```

Configuration:

```json
{
  "Auth": {
    "OAuth": {
      "Enabled": true,
      "Authority": "https://login.microsoftonline.com/{tenant-id}/v2.0",
      "ClientId": "hm-web-client-id",
      "Scopes": ["openid", "profile", "email"],
      "UsePkce": true,
      "CallbackPath": "/auth/oauth/callback",
      "ClaimRoleMapping": {
        "roles": {
          "HM.Admin": "Admin",
          "HM.Operator": "Operator",
          "HM.Viewer": "Viewer"
        }
      }
    }
  }
}
```

### 4.8 Session & Token Lifecycle

```
Login ──→ Access Token (15 min TTL) + Refresh Token (7 day TTL)
              │
              ├── API call → Gateway validates JWT signature + expiry
              │                   ├── Valid → route to Broker
              │                   └── Expired → 401 → client uses Refresh Token
              │
              ├── Refresh → hm-auth issues new Access Token
              │                   ├── Refresh valid → new tokens
              │                   └── Refresh expired / revoked → 401 → re-login
              │
              └── Logout / Admin revoke → refresh token blacklisted in SQL
```

| Token | Storage | TTL | Revocation |
|---|---|---|---|
| Access Token (JWT) | Browser memory (never localStorage) | 15 min | Short-lived; not individually revocable |
| Refresh Token | HttpOnly Secure cookie | 7 days | Revocable via SQL blacklist table |
| SignalR Token | Query string (WSS only) | Matches access token | Token rotation on reconnect |

### 4.9 Authorization Enforcement Points

| Layer | Mechanism |
|---|---|
| **Ingress** | TLS termination; no auth logic |
| **API Gateway** | JWT signature + expiry validation; reject expired/malformed tokens |
| **Web GUI** | Blazor `[Authorize]` attributes on pages; hide UI elements per permission |
| **Broker API** | `[Authorize(Policy = "...")]` per endpoint; permission-based policies |
| **Agent Gateway** | mTLS only (machine-to-machine); no user JWT |
| **SQL Server** | Application-level RBAC only; DB uses a single service account |

---

## 5. Data Tier — SQL Server Migration

### 5.1 Migration Strategy

| Aspect | SQLite (Current) | SQL Server (Target) |
|---|---|---|
| **Connection** | File-based, single-process | Network TCP, connection pooling |
| **Concurrency** | WAL (1 writer, N readers) | Full MVCC (snapshot isolation) |
| **Provider** | `Microsoft.EntityFrameworkCore.Sqlite` | `Microsoft.EntityFrameworkCore.SqlServer` |
| **Schema** | `EnsureCreated()` + migration | EF Core migrations, DBA-managed |
| **Audit immutability** | `SaveChanges` override | + SQL Server triggers as defense-in-depth |
| **Full-text search** | Not available | SQL Server FTS on audit `Detail` column |
| **Encryption** | File-level via OS ACL | TDE (Transparent Data Encryption) |

### 5.2 Database Schema Extensions

```sql
-- ═══════════════════════════════════════════════════════
--  New tables for auth, multi-tenancy, and sessions
-- ═══════════════════════════════════════════════════════

CREATE TABLE Users (
    Id              UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWSEQUENTIALID()  PRIMARY KEY,
    ExternalId      NVARCHAR(256)     NULL,      -- AD SID, SAML NameID, OAuth sub
    AuthProvider    NVARCHAR(50)      NOT NULL,   -- 'ActiveDirectory', 'SAML', 'OAuth', 'Local'
    Username        NVARCHAR(256)     NOT NULL  UNIQUE,
    Email           NVARCHAR(256)     NULL,
    DisplayName     NVARCHAR(256)     NOT NULL,
    PasswordHash    NVARCHAR(512)     NULL,      -- Argon2id; NULL for federated users
    IsActive        BIT               NOT NULL  DEFAULT 1,
    CreatedUtc      DATETIME2(7)      NOT NULL  DEFAULT SYSUTCDATETIME(),
    LastLoginUtc    DATETIME2(7)      NULL,
    LockedUntilUtc  DATETIME2(7)      NULL       -- Account lockout after failed attempts
);

CREATE TABLE Roles (
    Id              UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWSEQUENTIALID()  PRIMARY KEY,
    Name            NVARCHAR(100)     NOT NULL  UNIQUE,
    Description     NVARCHAR(500)     NULL,
    IsSystem        BIT               NOT NULL  DEFAULT 0  -- Viewer/Operator/Admin/Auditor
);

CREATE TABLE RolePermissions (
    RoleId          UNIQUEIDENTIFIER  NOT NULL  REFERENCES Roles(Id),
    Permission      NVARCHAR(100)     NOT NULL,  -- e.g. 'machines:write'
    PRIMARY KEY (RoleId, Permission)
);

CREATE TABLE UserRoles (
    UserId          UNIQUEIDENTIFIER  NOT NULL  REFERENCES Users(Id) ON DELETE CASCADE,
    RoleId          UNIQUEIDENTIFIER  NOT NULL  REFERENCES Roles(Id) ON DELETE CASCADE,
    AssignedUtc     DATETIME2(7)      NOT NULL  DEFAULT SYSUTCDATETIME(),
    AssignedBy      UNIQUEIDENTIFIER  NULL      REFERENCES Users(Id),
    PRIMARY KEY (UserId, RoleId)
);

CREATE TABLE RefreshTokens (
    Id              UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWSEQUENTIALID()  PRIMARY KEY,
    UserId          UNIQUEIDENTIFIER  NOT NULL  REFERENCES Users(Id) ON DELETE CASCADE,
    TokenHash       VARBINARY(64)     NOT NULL,  -- SHA-256 of the refresh token
    ExpiresUtc      DATETIME2(7)      NOT NULL,
    CreatedUtc      DATETIME2(7)      NOT NULL  DEFAULT SYSUTCDATETIME(),
    RevokedUtc      DATETIME2(7)      NULL,
    ReplacedByToken UNIQUEIDENTIFIER  NULL,      -- Token rotation chain
    CreatedByIp     NVARCHAR(45)      NULL
);

CREATE TABLE ExternalIdpConfigs (
    Id              UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWSEQUENTIALID()  PRIMARY KEY,
    ProviderType    NVARCHAR(50)      NOT NULL,  -- 'SAML', 'OAuth', 'ActiveDirectory'
    Name            NVARCHAR(100)     NOT NULL,  -- Display name
    ConfigJson      NVARCHAR(MAX)     NOT NULL,  -- Encrypted JSON config blob
    IsEnabled       BIT               NOT NULL  DEFAULT 1,
    CreatedUtc      DATETIME2(7)      NOT NULL  DEFAULT SYSUTCDATETIME(),
    ModifiedUtc     DATETIME2(7)      NULL
);

-- Existing table additions
ALTER TABLE AuditEvents ADD
    UserId          UNIQUEIDENTIFIER  NULL  REFERENCES Users(Id),
    IpAddress       NVARCHAR(45)      NULL,
    UserAgent       NVARCHAR(512)     NULL;

ALTER TABLE Jobs ADD
    SubmittedByUserId   UNIQUEIDENTIFIER  NULL  REFERENCES Users(Id);

-- ═══════════════════════════════════════════════════════
--  Indexes for auth-heavy queries
-- ═══════════════════════════════════════════════════════

CREATE INDEX IX_Users_ExternalId ON Users(AuthProvider, ExternalId) WHERE ExternalId IS NOT NULL;
CREATE INDEX IX_Users_Email ON Users(Email) WHERE Email IS NOT NULL;
CREATE INDEX IX_RefreshTokens_TokenHash ON RefreshTokens(TokenHash) WHERE RevokedUtc IS NULL;
CREATE INDEX IX_RefreshTokens_UserId ON RefreshTokens(UserId, ExpiresUtc);
CREATE INDEX IX_AuditEvents_UserId ON AuditEvents(UserId, TimestampUtc DESC);

-- Audit immutability trigger (defense-in-depth alongside EF Core override)
CREATE TRIGGER TR_AuditEvents_Immutable
ON AuditEvents
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    RAISERROR('Audit events are immutable. Updates and deletes are prohibited.', 16, 1);
    ROLLBACK TRANSACTION;
END;
```

### 5.3 Connection Configuration

```json
{
  "ConnectionStrings": {
    "HomeManagement": "Server=sql-server.corp.local;Database=homemanagement;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity"
  },
  "Database": {
    "CommandTimeout": 30,
    "MaxRetryCount": 3,
    "EnableSensitiveDataLogging": false,
    "MigrationsAssembly": "HomeManagement.Data.SqlServer"
  }
}
```

EF Core provider swap:

```csharp
// Desktop (SQLite) — existing
services.AddDbContext<HomeManagementDbContext>(options =>
    options.UseSqlite(connectionString));

// Platform (SQL Server) — new
services.AddDbContext<HomeManagementDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
    {
        sql.MigrationsAssembly("HomeManagement.Data.SqlServer");
        sql.EnableRetryOnFailure(maxRetryCount: 3);
        sql.CommandTimeout(30);
    }));
```

### 5.4 Data Migration Path

```
Phase 1: Schema deployment
  dotnet ef migrations add PlatformV1 --project HomeManagement.Data.SqlServer
  dotnet ef database update

Phase 2: Data migration (one-time)
  1. Export SQLite → CSV/JSON via existing CSV export infrastructure
  2. Import into SQL Server via bulk insert tool
  3. Validate row counts and HMAC chain integrity
  4. Generate new Users/Roles from AD sync

Phase 3: Switchover
  1. Set broker config ConnectionStrings:HomeManagement → SQL Server
  2. Deploy new pods
  3. Run audit chain verification
  4. Decommission SQLite references
```

---

## 6. Web GUI Architecture

### 6.1 Technology Stack

| Layer | Technology | Rationale |
|---|---|---|
| **Rendering** | Blazor Server (.NET 8) | Server-side rendering; no WASM download; rich .NET ecosystem; SignalR for real-time |
| **Styling** | Tailwind CSS + Radix UI | Utility-first; accessible primitives; consistent design system |
| **State** | Fluxor (Redux for Blazor) | Predictable unidirectional state management for complex pages |
| **Real-time** | SignalR (built into Blazor Server) | Agent events, job progress, audit stream pushed to browser |
| **HTTP Client** | `HttpClient` + Refit | Typed API clients to Broker endpoints |
| **Auth** | ASP.NET Core Identity + cookie auth | Blazor Server uses cookies; thin wrapper around JWT from hm-auth |

### 6.2 Page Inventory

| Page | Route | Auth Required | Min Role | Real-time |
|---|---|---|---|---|
| Login | `/login` | No | — | — |
| Dashboard | `/` | Yes | Viewer | Agent status, job progress, health |
| Machines | `/machines` | Yes | Viewer | Agent connection events |
| Machine Detail | `/machines/{id}` | Yes | Viewer | Live metadata refresh |
| Patching | `/patching` | Yes | Operator | Scan/apply progress |
| Services | `/services` | Yes | Operator | Service state changes |
| Jobs | `/jobs` | Yes | Viewer | Live job progress |
| Job Detail | `/jobs/{id}` | Yes | Viewer | Per-machine results stream |
| Credentials | `/credentials` | Yes | Admin | — |
| Audit Log | `/audit` | Yes | Auditor | Live event stream |
| Settings | `/settings` | Yes | Admin | — |
| User Management | `/admin/users` | Yes | Admin | — |
| Role Management | `/admin/roles` | Yes | Admin | — |

### 6.3 Real-Time Architecture

```
┌─────────────┐         ┌──────────────┐         ┌──────────────┐
│  Browser    │ SignalR  │  hm-web      │  HTTP   │  hm-broker   │
│  (Blazor    │◄────────→│  (Blazor     │────────→│              │
│   circuit)  │  WSS     │   Server)    │         │  REST API    │
└─────────────┘         └──────┬───────┘         └──────┬───────┘
                               │                        │
                               │    SignalR Backplane    │
                               │    (Redis / SQL)        │
                               │                        │
                        ┌──────┴────────────────────────┴──────────────┐
                        │                                               │
                        │  Event Bus (IHubContext<EventHub>)             │
                        │  ├─ JobProgressEvent      (from Broker)       │
                        │  ├─ AgentConnectionEvent   (from AgentGW)     │
                        │  ├─ CommandCompletedEvent   (from Broker)     │
                        │  ├─ AuditEvent              (from Broker)     │
                        │  └─ HealthStatusEvent       (from Broker)     │
                        │                                               │
                        └───────────────────────────────────────────────┘
```

Events flow:
1. **Broker** publishes domain events to the SignalR backplane (Redis or SQL Server)
2. **Web GUI** Blazor Server subscribes to relevant groups per authenticated user
3. **Browser** receives real-time updates via the existing Blazor Server SignalR circuit
4. Components re-render automatically via Blazor's diffing engine

### 6.4 API Client Layer

```csharp
// Refit-based typed API client (replaces direct DI injection of services)
public interface IBrokerApi
{
    // Machines
    [Get("/api/machines")]
    Task<PagedResult<MachineSummary>> ListMachinesAsync(MachineQuery query, CancellationToken ct);

    [Post("/api/machines")]
    Task<Machine> AddMachineAsync(MachineCreateRequest request, CancellationToken ct);

    // Patching
    [Post("/api/patching/scan")]
    Task<Guid> ScanForPatchesAsync(PatchScanRequest request, CancellationToken ct);

    [Post("/api/patching/apply")]
    Task<JobId> ApplyPatchesAsync(PatchApplyRequest request, CancellationToken ct);

    // Services
    [Get("/api/services/{machineId}")]
    Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(Guid machineId, CancellationToken ct);

    [Post("/api/services/{machineId}/control")]
    Task<Guid> ControlServiceAsync(Guid machineId, ServiceControlRequest request, CancellationToken ct);

    // Jobs
    [Get("/api/jobs")]
    Task<PagedResult<JobSummary>> ListJobsAsync(JobQuery query, CancellationToken ct);

    [Post("/api/jobs")]
    Task<JobId> SubmitJobAsync(JobDefinition job, CancellationToken ct);

    [Delete("/api/jobs/{jobId}")]
    Task CancelJobAsync(Guid jobId, CancellationToken ct);

    // Audit
    [Get("/api/audit")]
    Task<PagedResult<AuditEvent>> QueryAuditAsync(AuditQuery query, CancellationToken ct);
}
```

---

## 7. Broker Service API

The Broker exposes a REST API consumed by the Web GUI and the API Gateway.
This is the **only** entry point for domain operations in the platform deployment.

### 7.1 API Endpoints

| Method | Route | Auth | Policy | Description |
|---|---|---|---|---|
| **Machines** | | | | |
| GET | `/api/machines` | JWT | `machines:read` | Paged machine list |
| GET | `/api/machines/{id}` | JWT | `machines:read` | Machine detail |
| POST | `/api/machines` | JWT | `machines:write` | Add machine |
| PUT | `/api/machines/{id}` | JWT | `machines:write` | Update machine |
| DELETE | `/api/machines/{id}` | JWT | `machines:write` | Soft-delete machine |
| POST | `/api/machines/discover` | JWT | `machines:write` | CIDR discovery |
| POST | `/api/machines/{id}/test` | JWT | `machines:read` | Test connection |
| **Patching** | | | | |
| POST | `/api/patching/scan` | JWT | `patches:read` | Scan for patches |
| POST | `/api/patching/apply` | JWT | `patches:apply` | Apply patches (creates job) |
| GET | `/api/patching/{machineId}/history` | JWT | `patches:read` | Patch history |
| **Services** | | | | |
| GET | `/api/services/{machineId}` | JWT | `services:read` | List services |
| POST | `/api/services/{machineId}/control` | JWT | `services:control` | Start/stop/restart |
| **Jobs** | | | | |
| GET | `/api/jobs` | JWT | `jobs:read` | Paged job list |
| GET | `/api/jobs/{id}` | JWT | `jobs:read` | Job detail + results |
| POST | `/api/jobs` | JWT | `jobs:submit` | Submit job |
| DELETE | `/api/jobs/{id}` | JWT | `jobs:cancel` | Cancel job |
| POST | `/api/jobs/schedule` | JWT | `jobs:submit` | Create scheduled job |
| **Credentials** | | | | |
| GET | `/api/credentials` | JWT | `credentials:read` | List (metadata only) |
| POST | `/api/credentials` | JWT | `credentials:write` | Add credential |
| DELETE | `/api/credentials/{id}` | JWT | `credentials:write` | Remove credential |
| **Audit** | | | | |
| GET | `/api/audit` | JWT | `audit:read` | Query audit events |
| POST | `/api/audit/export` | JWT | `audit:export` | Export to CSV |
| **Admin** | | | | |
| GET | `/api/admin/users` | JWT | `admin:users` | List users |
| POST | `/api/admin/users` | JWT | `admin:users` | Create local user |
| PUT | `/api/admin/users/{id}/roles` | JWT | `admin:users` | Assign roles |
| GET | `/api/admin/roles` | JWT | `admin:users` | List roles |
| POST | `/api/admin/roles` | JWT | `admin:users` | Create custom role |
| GET | `/api/admin/health` | JWT | `admin:settings` | System health |

### 7.2 Internal Communication

```
Web GUI ──HTTP──→ API Gateway ──HTTP──→ Broker Service
                                              │
                                              ├──→ SQL Server (EF Core)
                                              ├──→ Agent Gateway (gRPC internal)
                                              └──→ SignalR Backplane (events)

Agent Gateway ──gRPC (mTLS)──→ Agents
              ──HTTP──→ Broker Service (command results callback)
```

| From | To | Protocol | Auth |
|---|---|---|---|
| Browser → Web GUI | SignalR (WSS) | Cookie auth (HTTPS) |
| Web GUI → Gateway | HTTP/2 | JWT (service-to-service) |
| Gateway → Broker | HTTP/2 | JWT (forwarded from user) |
| Gateway → Auth | HTTP/2 | Internal mTLS |
| Broker → SQL Server | TDS (TCP 1433) | Managed Identity or SQL auth |
| Broker → Agent GW | HTTP/2 | Internal mTLS |
| Agent GW → Agents | gRPC (H2) | mTLS (agent certs) |
| Broker → SignalR backplane | Redis / SQL | Connection string secret |

---

## 8. Kubernetes Deployment

### 8.1 Resource Manifests (Summary)

```yaml
# ─── Namespace ───
apiVersion: v1
kind: Namespace
metadata:
  name: homemanagement
  labels:
    istio-injection: enabled  # Optional: service mesh

---
# ─── Broker Deployment ───
apiVersion: apps/v1
kind: Deployment
metadata:
  name: hm-broker
  namespace: homemanagement
spec:
  replicas: 1            # Leader election for HA; scale orchestrator workers via config
  selector:
    matchLabels:
      app: hm-broker
  template:
    metadata:
      labels:
        app: hm-broker
    spec:
      serviceAccountName: hm-broker-sa
      containers:
        - name: broker
          image: ghcr.io/org/hm-broker:latest
          ports:
            - containerPort: 8082
              name: http
            - containerPort: 9090
              name: metrics
          env:
            - name: ConnectionStrings__HomeManagement
              valueFrom:
                secretKeyRef:
                  name: hm-db-secret
                  key: connection-string
            - name: Auth__JwtSigningKey
              valueFrom:
                secretKeyRef:
                  name: hm-auth-secret
                  key: jwt-signing-key
          resources:
            requests:
              cpu: 500m
              memory: 512Mi
            limits:
              cpu: "2"
              memory: 2Gi
          livenessProbe:
            httpGet:
              path: /healthz
              port: http
            initialDelaySeconds: 10
          readinessProbe:
            httpGet:
              path: /readyz
              port: http
            initialDelaySeconds: 5
          volumeMounts:
            - name: vault-enc
              mountPath: /data/vault
              readOnly: false
      volumes:
        - name: vault-enc
          persistentVolumeClaim:
            claimName: hm-vault-pvc

---
# ─── Web GUI Deployment ───
apiVersion: apps/v1
kind: Deployment
metadata:
  name: hm-web
  namespace: homemanagement
spec:
  replicas: 2
  selector:
    matchLabels:
      app: hm-web
  template:
    spec:
      containers:
        - name: web
          image: ghcr.io/org/hm-web:latest
          ports:
            - containerPort: 8080
          env:
            - name: BrokerApi__BaseUrl
              value: http://hm-broker:8082
            - name: Auth__Authority
              value: http://hm-auth:8083
          resources:
            requests:
              cpu: 250m
              memory: 256Mi
            limits:
              cpu: "1"
              memory: 1Gi

---
# ─── Agent Gateway Deployment ───
apiVersion: apps/v1
kind: Deployment
metadata:
  name: hm-agent-gw
  namespace: homemanagement
spec:
  replicas: 1
  selector:
    matchLabels:
      app: hm-agent-gw
  template:
    spec:
      containers:
        - name: agent-gw
          image: ghcr.io/org/hm-agent-gw:latest
          ports:
            - containerPort: 9444
              name: grpc
          volumeMounts:
            - name: agent-certs
              mountPath: /certs
              readOnly: true
      volumes:
        - name: agent-certs
          secret:
            secretName: hm-agent-gw-certs

---
# ─── Services ───
apiVersion: v1
kind: Service
metadata:
  name: hm-broker
  namespace: homemanagement
spec:
  selector:
    app: hm-broker
  ports:
    - port: 8082
      targetPort: http
      name: http

---
apiVersion: v1
kind: Service
metadata:
  name: hm-agent-gw
  namespace: homemanagement
spec:
  type: LoadBalancer   # External: agents connect from outside cluster
  selector:
    app: hm-agent-gw
  ports:
    - port: 9444
      targetPort: grpc
      name: grpc

---
# ─── Ingress ───
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: hm-ingress
  namespace: homemanagement
  annotations:
    cert-manager.io/cluster-issuer: letsencrypt-prod
    nginx.ingress.kubernetes.io/ssl-redirect: "true"
spec:
  tls:
    - hosts:
        - hm.corp.local
      secretName: hm-tls
  rules:
    - host: hm.corp.local
      http:
        paths:
          - path: /api
            pathType: Prefix
            backend:
              service:
                name: hm-gateway
                port:
                  number: 8081
          - path: /
            pathType: Prefix
            backend:
              service:
                name: hm-web
                port:
                  number: 8080
```

### 8.2 Health Checks

| Service | Liveness | Readiness | Startup |
|---|---|---|---|
| **Broker** | `/healthz` (DB ping + queue alive) | `/readyz` (DB + agent-gw reachable) | 30s grace |
| **Web GUI** | `/healthz` (process alive) | `/readyz` (broker API reachable) | 15s grace |
| **Auth** | `/healthz` (process alive) | `/readyz` (DB + signing key loaded) | 15s grace |
| **Agent GW** | `/healthz` (gRPC server alive) | `/readyz` (certs loaded + broker reachable) | 20s grace |

### 8.3 Secrets Management

| Secret | Source | Rotation |
|---|---|---|
| SQL Server connection string | K8s Secret (sealed) or Azure Key Vault | On provider credential rotation |
| JWT signing key (RSA-2048 or Ed25519) | K8s Secret or Vault | Every 90 days; old keys kept for validation |
| Agent CA cert + server cert | cert-manager (auto-renew) | Auto-rotated 30 days before expiry |
| SAML signing cert | K8s Secret | Annual rotation |
| Vault encryption master key | Never in K8s — derived from admin password at runtime | Password change = re-encryption |

---

## 9. Migration Phases

### Phase A — Foundation (Parallel Development)

```
Duration: 4-6 weeks

┌─────────────────────────────────────────────────────────┐
│ 1. Create hm-broker ASP.NET Core Web API project        │
│    - Move domain services into Minimal API endpoints    │
│    - Reuse ALL existing module implementations          │
│    - Add EF Core SQL Server provider                    │
│                                                         │
│ 2. Create hm-auth ASP.NET Core project                  │
│    - JWT issuance and validation                        │
│    - Active Directory LDAP integration                  │
│    - User/Role CRUD                                     │
│                                                         │
│ 3. Create hm-web Blazor Server project                  │
│    - Port ViewModel logic to Blazor components          │
│    - Refit client for Broker API                        │
│    - Cookie-based auth with JWT backing                 │
│                                                         │
│ 4. Set up Kubernetes manifests                          │
│    - Dev namespace with SQL Server container             │
│    - CI/CD pipeline (build → test → push images)        │
│                                                         │
│ Desktop GUI continues to work unchanged.                │
└─────────────────────────────────────────────────────────┘
```

### Phase B — Auth Expansion

```
Duration: 2-3 weeks

┌─────────────────────────────────────────────────────────┐
│ 1. Add SAML 2.0 SP support to hm-auth                  │
│    - Sustainsys.Saml2 or ITfoxtec.Identity.Saml2        │
│    - Configurable IdP metadata import                   │
│    - Attribute → role mapping                           │
│                                                         │
│ 2. Add OAuth 2.0 / OIDC support to hm-auth             │
│    - ASP.NET Core OpenIdConnect handler                 │
│    - Authorization Code + PKCE flow                     │
│    - Claim → role mapping                               │
│                                                         │
│ 3. Admin UI for IdP configuration                       │
│    - CRUD external IdP configs (encrypted in DB)        │
│    - Test login flow per provider                       │
│                                                         │
│ 4. Audit: all auth events recorded with user identity   │
└─────────────────────────────────────────────────────────┘
```

### Phase C — Production Hardening

```
Duration: 2-3 weeks

┌─────────────────────────────────────────────────────────┐
│ 1. HPA (Horizontal Pod Autoscaler) for Web + Gateway    │
│ 2. Leader election for Broker + Agent Gateway            │
│ 3. SQL Server HA (Always On / Azure SQL failover)       │
│ 4. Centralized logging (Seq / Loki / ELK)               │
│ 5. Prometheus + Grafana metrics dashboards              │
│ 6. Penetration testing + OWASP ZAP scan                 │
│ 7. Load testing (k6) — target 100 concurrent users     │
│ 8. Disaster recovery: backup/restore procedures         │
│ 9. Desktop GUI deprecation notice + migration guide     │
└─────────────────────────────────────────────────────────┘
```

### Phase D — Independent Execution Platform

```
Duration: 4-6 weeks (post-Phase C)

┌─────────────────────────────────────────────────────────┐
│ 1. Multi-tenant support                                 │
│    - TenantId column on all tables                      │
│    - Tenant-scoped RBAC                                 │
│    - Tenant isolation at API layer                      │
│                                                         │
│ 2. API-first external access                            │
│    - OpenAPI spec published                             │
│    - API key + OAuth2 client_credentials for automation │
│    - Rate limiting per client                           │
│                                                         │
│ 3. Plugin / extension framework                         │
│    - Custom command handlers via DI registration        │
│    - Webhook notifications for events                   │
│    - Custom job types via plugin assemblies              │
│                                                         │
│ 4. Federation                                           │
│    - Broker-to-broker communication for multi-site      │
│    - Hierarchical agent management                      │
│    - Cross-site job orchestration                       │
└─────────────────────────────────────────────────────────┘
```

---

## 10. New Project Structure

```
homeManagement.sln
├── src/
│   ├── HomeManagement.Abstractions/        # Unchanged — all interfaces
│   ├── HomeManagement.Data/                # EF Core entities, repos (provider-agnostic)
│   ├── HomeManagement.Data.SqlServer/      # NEW — SQL Server migrations + provider config
│   ├── HomeManagement.Core/                # Module registration, health, logging
│   │
│   ├── HomeManagement.Vault/               # Unchanged
│   ├── HomeManagement.Transport/           # Unchanged (CommandBrokerService, providers)
│   ├── HomeManagement.Patching/            # Unchanged
│   ├── HomeManagement.Services/            # Unchanged
│   ├── HomeManagement.Inventory/           # Unchanged
│   ├── HomeManagement.Orchestration/       # Unchanged
│   ├── HomeManagement.Auditing/            # Unchanged
│   │
│   ├── HomeManagement.Auth/                # NEW — Auth service library
│   │   ├── JwtTokenService.cs
│   │   ├── ActiveDirectoryProvider.cs
│   │   ├── SamlProvider.cs
│   │   ├── OAuthProvider.cs
│   │   ├── LocalAuthProvider.cs
│   │   ├── RbacService.cs
│   │   └── SessionManager.cs
│   │
│   ├── HomeManagement.Auth.Host/           # NEW — Auth service ASP.NET host
│   │   ├── Program.cs
│   │   ├── Endpoints/
│   │   │   ├── LoginEndpoints.cs
│   │   │   ├── TokenEndpoints.cs
│   │   │   ├── SamlEndpoints.cs
│   │   │   ├── OAuthEndpoints.cs
│   │   │   └── UserAdminEndpoints.cs
│   │   └── Dockerfile
│   │
│   ├── HomeManagement.Broker.Host/         # NEW — Broker service ASP.NET host
│   │   ├── Program.cs
│   │   ├── Endpoints/
│   │   │   ├── MachineEndpoints.cs
│   │   │   ├── PatchingEndpoints.cs
│   │   │   ├── ServiceEndpoints.cs
│   │   │   ├── JobEndpoints.cs
│   │   │   ├── CredentialEndpoints.cs
│   │   │   ├── AuditEndpoints.cs
│   │   │   └── HealthEndpoints.cs
│   │   ├── Hubs/
│   │   │   └── EventHub.cs              # SignalR hub for real-time events
│   │   └── Dockerfile
│   │
│   ├── HomeManagement.Web/                 # NEW — Blazor Server web GUI
│   │   ├── Program.cs
│   │   ├── App.razor
│   │   ├── Pages/
│   │   │   ├── Login.razor
│   │   │   ├── Dashboard.razor
│   │   │   ├── Machines.razor
│   │   │   ├── MachineDetail.razor
│   │   │   ├── Patching.razor
│   │   │   ├── Services.razor
│   │   │   ├── Jobs.razor
│   │   │   ├── JobDetail.razor
│   │   │   ├── Credentials.razor
│   │   │   ├── AuditLog.razor
│   │   │   ├── Settings.razor
│   │   │   └── Admin/
│   │   │       ├── Users.razor
│   │   │       └── Roles.razor
│   │   ├── Components/
│   │   │   ├── Layout/
│   │   │   │   ├── MainLayout.razor
│   │   │   │   ├── NavMenu.razor
│   │   │   │   └── StatusBar.razor
│   │   │   ├── MachinePickerComponent.razor
│   │   │   ├── ProgressOverlay.razor
│   │   │   ├── ErrorBanner.razor
│   │   │   └── DataGridFilter.razor
│   │   ├── Services/
│   │   │   ├── BrokerApiClient.cs       # Refit IBrokerApi registration
│   │   │   ├── AuthStateProvider.cs     # Custom AuthenticationStateProvider
│   │   │   └── EventHubClient.cs        # SignalR client for real-time events
│   │   └── Dockerfile
│   │
│   ├── HomeManagement.AgentGateway.Host/   # NEW — Agent Gateway ASP.NET host
│   │   ├── Program.cs
│   │   ├── Dockerfile
│   │   └── Services/
│   │       └── AgentGatewayGrpcService.cs
│   │
│   ├── HomeManagement.Gateway/             # NEW — YARP API Gateway
│   │   ├── Program.cs
│   │   ├── Dockerfile
│   │   └── yarp.json                     # Route configuration
│   │
│   ├── HomeManagement.Gui/                # EXISTING — Desktop GUI (maintained during transition)
│   └── HomeManagement.Agent/              # EXISTING — Agent binary (unchanged)
│
├── deploy/
│   ├── kubernetes/
│   │   ├── namespace.yaml
│   │   ├── broker-deployment.yaml
│   │   ├── web-deployment.yaml
│   │   ├── auth-deployment.yaml
│   │   ├── agent-gw-deployment.yaml
│   │   ├── gateway-deployment.yaml
│   │   ├── services.yaml
│   │   ├── ingress.yaml
│   │   ├── secrets.yaml
│   │   ├── pvc.yaml
│   │   └── hpa.yaml
│   ├── docker/
│   │   └── docker-compose.yaml          # Local dev stack
│   └── helm/
│       └── homemanagement/               # Helm chart (future)
│
└── tests/
    ├── (existing 6 test projects)
    ├── HomeManagement.Auth.Tests/         # NEW
    ├── HomeManagement.Broker.Host.Tests/  # NEW — API endpoint integration tests
    └── HomeManagement.Web.Tests/          # NEW — Blazor component tests
```

---

## 11. Deployment Topology

```
┌───────────────────────────────────────────────────────────────────┐
│  PRODUCTION CLUSTER (3+ nodes)                                     │
│                                                                    │
│  Node 1 (Worker)          Node 2 (Worker)         Node 3 (Worker) │
│  ┌──────────────────┐    ┌──────────────────┐    ┌──────────────┐ │
│  │ hm-web (pod 1)   │    │ hm-web (pod 2)   │    │ hm-broker    │ │
│  │ hm-gateway (1)   │    │ hm-gateway (2)   │    │ hm-agent-gw  │ │
│  │ hm-auth (pod 1)  │    │ hm-auth (pod 2)  │    │ hm-auth (3)  │ │
│  └──────────────────┘    └──────────────────┘    └──────────────┘ │
│                                                                    │
│  ┌────────────────────────────────────────────────────────────┐   │
│  │  Infrastructure: cert-manager, ingress-nginx, metrics-server│   │
│  └────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────┘
         │                    │                    │
         └────────────────────┴────────────────────┘
                              │
              ┌───────────────┴───────────────┐
              │  SQL Server (External)         │
              │  ├─ Primary (read-write)       │
              │  └─ Secondary (read replica)   │
              │                                │
              │  Options:                      │
              │  • Azure SQL Managed Instance  │
              │  • SQL Server on VM            │
              │  • SQL Server container (dev)  │
              └────────────────────────────────┘
```

---

## 12. Security Considerations

### 12.1 Network Policies

```yaml
# Only allow traffic between homemanagement pods
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: hm-internal-only
  namespace: homemanagement
spec:
  podSelector: {}
  policyTypes: [Ingress, Egress]
  ingress:
    - from:
        - namespaceSelector:
            matchLabels:
              name: homemanagement
    - from:
        - namespaceSelector:
            matchLabels:
              name: ingress-nginx
  egress:
    - to:
        - namespaceSelector:
            matchLabels:
              name: homemanagement
    - to:
        - ipBlock:
            cidr: 10.0.0.0/8     # SQL Server + managed machines
    - to:
        - ipBlock:
            cidr: 0.0.0.0/0      # External IdPs (SAML/OAuth)
      ports:
        - port: 443
          protocol: TCP
```

### 12.2 Security Checklist

| Area | Control |
|---|---|
| **Transport** | All inter-pod: mTLS (Istio or cert-manager); Ingress: TLS 1.3 |
| **Secrets** | Never in environment variables long-term; use K8s Secrets with encryption at rest; prefer external vault |
| **Containers** | Non-root user; read-only root filesystem; no privilege escalation |
| **SQL Server** | TDE enabled; Managed Identity auth (no passwords); network ACL to cluster CIDR only |
| **JWT** | RSA-2048 or Ed25519 signing; short-lived (15 min); refresh tokens rotated on use |
| **SAML** | Signed assertions required; replay detection via `InResponseTo`; clock skew ≤ 5 min |
| **OAuth** | PKCE required; state parameter validated; redirect URI allowlist |
| **Audit** | All auth events logged; SQL trigger prevents audit deletion; HMAC chain preserved |
| **RBAC** | Principle of least privilege; Viewer default; Admin actions require re-authentication |
| **Rate Limiting** | Per-user: 100 req/min; Per-IP: 200 req/min; Auth endpoints: 10 attempts/min |

---

## 13. Compatibility & Coexistence

During migration, the desktop GUI and web platform coexist:

```
┌─────────────────────────────────────────────────────────┐
│  Desktop GUI (Avalonia)                                  │
│  ├─ Connects directly to embedded SQLite                │
│  ├─ Hosts gRPC server locally (:9444)                   │
│  ├─ Single-user, no auth                                │
│  └─ Fully functional standalone                         │
├─────────────────────────────────────────────────────────┤
│  Web Platform (Kubernetes)                               │
│  ├─ Connects to SQL Server                              │
│  ├─ Agent Gateway in K8s (:9444 via LoadBalancer)       │
│  ├─ Multi-user with RBAC                                │
│  └─ Requires network infrastructure                     │
└─────────────────────────────────────────────────────────┘

Agents can connect to EITHER the desktop GUI or the K8s Agent Gateway.
Configuration switch in hm-agent.json:
  "ControlServer": "desktop:9444"    ← Desktop mode
  "ControlServer": "k8s-lb:9444"    ← Platform mode
```

Interface compatibility guarantee:
- All `HomeManagement.Abstractions` interfaces remain unchanged
- Module implementations (Vault, Patching, Services, etc.) are reused as-is
- Only the hosting layer changes — new ASP.NET Core hosts wrap existing services
- Agent binary is 100% compatible — no changes needed

---

## 14. Decision Log

| # | Decision | Rationale | Alternatives Considered |
|---|---|---|---|
| D1 | Blazor Server over Blazor WASM | No WASM download; SignalR built-in; full .NET API access; simpler auth | WASM (offline capable but larger payload, complex auth), React (separate stack) |
| D2 | SQL Server over PostgreSQL | Enterprise licensing alignment; AD integration; TDE; Always On HA | PostgreSQL (free, cross-platform), CockroachDB (distributed) |
| D3 | YARP gateway over Ocelot | First-party Microsoft; better perf; native .NET integration | Ocelot (more features), Envoy (complex), Kong (external) |
| D4 | JWT over session cookies for API | Stateless validation; service-to-service compatible; standard | Opaque tokens (require token introspection), Session cookies (not API-friendly) |
| D5 | Single Broker pod over microservices | Domain services are tightly coupled via `IUnitOfWork`; splitting creates distributed transaction complexity | Per-domain microservices (eventual consistency overhead) |
| D6 | Leader election over active-active Broker | CommandBrokerService has in-memory `Channel<T>` state; active-active requires distributed queue | Redis Streams (adds infra), Azure Service Bus (cloud-only) |
| D7 | Refit over raw HttpClient | Typed interfaces match existing Abstractions; compile-time validation; minimal boilerplate | gRPC (heavier for browser), raw HttpClient (repetitive) |
