# DeCloud Marketplace System - Complete Design
**Created:** 2026-01-30  
**Status:** Design Phase  
**Version:** 1.0

---

## Table of Contents
1. [Vision & Goals](#vision--goals)
2. [Architecture Overview](#architecture-overview)
3. [Data Models](#data-models)
4. [Template Types](#template-types)
5. [Backend API Design](#backend-api-design)
6. [Frontend Design](#frontend-design)
7. [User Flows](#user-flows)
8. [Cloud-Init Integration](#cloud-init-integration)
9. [Community Features](#community-features)
10. [Security & Validation](#security--validation)
11. [Implementation Phases](#implementation-phases)

---

## Vision & Goals

### Mission Statement
Create a **Minecraft-like marketplace** where users can:
- Browse and deploy pre-configured VMs (Templates)
- Deploy multi-VM solutions (Solution Templates)
- Create and share their own templates
- Discover community-created setups
- Deploy complex infrastructure with one click

### Core Principles
1. **Simplicity First** - Deploy in 3 clicks or less
2. **Community-Driven** - Users create the best templates
3. **Composability** - Templates reference other templates
4. **Permissionless** - No approval needed to create/share
5. **Transparency** - All code visible, no black boxes

### Success Metrics
- Templates deployed per week
- Community template submissions per week
- Average time from "discover" to "deployed"
- Template re-use rate (forks, deployments)

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                     MARKETPLACE FRONTEND                    │
│  ┌──────────────┐  ┌──────────────┐  ┌─────────────────┐  │
│  │   Explore    │  │  My Creations│  │  Community      │  │
│  │  Templates   │  │  & Favorites │  │  Leaderboard    │  │
│  └──────────────┘  └──────────────┘  └─────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│                     MARKETPLACE API                         │
│  ┌─────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │  Templates  │  │   Solutions  │  │   Deployments    │  │
│  │   CRUD      │  │    CRUD      │  │   Orchestration  │  │
│  └─────────────┘  └──────────────┘  └──────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                            │
                ┌───────────┴───────────┐
                ▼                       ▼
┌─────────────────────────┐  ┌────────────────────────────┐
│   MONGODB COLLECTIONS   │  │   EXISTING VM SERVICE      │
│  • vm_templates         │  │  • CreateVmAsync()         │
│  • solution_templates   │  │  • Node Scheduling         │
│  • template_ratings     │  │  • Resource Allocation     │
│  • template_deployments │  │  • Cloud-init Processing   │
└─────────────────────────┘  └────────────────────────────┘
```

### Key Components

**1. VM Templates**
- Single VM configurations
- Cloud-init scripts
- Resource requirements
- Categorization & tags

**2. Solution Templates**
- Multi-VM orchestration
- VM dependencies
- Network configurations
- Deployment order

**3. Marketplace API**
- Template CRUD operations
- Search & filtering
- Deployment orchestration
- Rating & reviews

**4. Frontend**
- Browse/discover interface
- Template creation wizard
- One-click deployment
- Community features

---

## Data Models

### 1. VmTemplate

```csharp
public class VmTemplate
{
    // Identity
    public string Id { get; set; }                    // "01hsxx..." (ULID)
    public string Name { get; set; }                  // "Stable Diffusion WebUI"
    public string Slug { get; set; }                  // "stable-diffusion-webui"
    public string Version { get; set; }               // "1.6.0"
    
    // Metadata
    public string Description { get; set; }           // Short description (280 chars)
    public string LongDescription { get; set; }       // Full markdown description
    public string Category { get; set; }              // "ai-ml", "web-apps", "dev-tools"
    public List<string> Tags { get; set; }            // ["gpu", "stable-diffusion", "ai"]
    public string IconUrl { get; set; }               // Template icon/logo
    public List<string> Screenshots { get; set; }     // Screenshot URLs
    
    // Author & Attribution
    public string AuthorId { get; set; }              // User wallet address
    public string AuthorName { get; set; }            // Display name
    public string License { get; set; }               // "MIT", "GPL-3.0", "Apache-2.0"
    public string SourceUrl { get; set; }             // GitHub repo, docs URL
    
    // VM Specification
    public VmSpec DefaultSpec { get; set; }           // CPU, memory, disk requirements
    public VmSpec MinimumSpec { get; set; }           // Minimum viable spec
    public VmSpec RecommendedSpec { get; set; }       // Optimal performance spec
    
    // Requirements
    public bool RequiresGpu { get; set; }             // Needs GPU
    public string GpuRequirement { get; set; }        // "RTX 3060 or better"
    public List<string> RequiredCapabilities { get; set; } // ["nvme", "high-bandwidth"]
    public Dictionary<string, string> EnvironmentVariables { get; set; }
    
    // Cloud-Init Configuration
    public string CloudInitTemplate { get; set; }     // Full cloud-init YAML
    public CloudInitType CloudInitType { get; set; }  // Inline, URL, or Base64
    public List<string> PackagesToInstall { get; set; }
    public List<string> StartupCommands { get; set; }
    
    // Access & Ports
    public List<PortMapping> ExposedPorts { get; set; }
    public string DefaultAccessUrl { get; set; }      // "https://{vm-id}.decloud.app:7860"
    public string DefaultUsername { get; set; }       // If app has default credentials
    public bool UseGeneratedPassword { get; set; }    // Use DeCloud's password system
    
    // Marketplace Stats
    public int DeploymentCount { get; set; }          // Total deployments
    public int ActiveDeployments { get; set; }        // Currently running
    public double AverageRating { get; set; }         // 0-5 stars
    public int RatingCount { get; set; }              // Number of ratings
    public int ForkCount { get; set; }                // Times forked/cloned
    public int ViewCount { get; set; }                // Page views
    
    // Status & Moderation
    public TemplateStatus Status { get; set; }        // Draft, Published, Archived, Flagged
    public TemplateVisibility Visibility { get; set; } // Public, Unlisted, Private
    public bool IsFeatured { get; set; }              // Featured by platform
    public bool IsVerified { get; set; }              // Verified by DeCloud team
    public bool IsCommunityPick { get; set; }         // Community top pick
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastDeployedAt { get; set; }
    public DateTime? FeaturedAt { get; set; }
    
    // Versioning & History
    public string ParentTemplateId { get; set; }      // If forked from another
    public List<string> VersionHistory { get; set; }  // Previous version IDs
    public string ChangelogUrl { get; set; }
    
    // Pricing (Optional - Future Feature)
    public decimal? AuthorFee { get; set; }           // % fee or fixed USDC
    public bool IsFreemium { get; set; }              // Free tier available
}

public enum TemplateStatus
{
    Draft,          // Being created, not published
    Published,      // Live in marketplace
    Archived,       // No longer maintained
    Flagged,        // Under review
    Rejected        // Violation of terms
}

public enum TemplateVisibility
{
    Public,         // Visible to all
    Unlisted,       // Only via direct link
    Private         // Only author can see
}

public enum CloudInitType
{
    Inline,         // Stored in template
    Url,            // Downloaded from URL
    Base64          // Base64 encoded
}

public class PortMapping
{
    public int Port { get; set; }
    public string Protocol { get; set; }        // "http", "https", "tcp", "udp"
    public string Description { get; set; }     // "Web UI", "API Endpoint"
    public bool IsPublic { get; set; }          // Expose via ingress
}
```

### 2. SolutionTemplate

```csharp
public class SolutionTemplate
{
    // Identity
    public string Id { get; set; }
    public string Name { get; set; }                  // "WordPress + MySQL + Redis"
    public string Slug { get; set; }
    public string Version { get; set; }
    
    // Metadata (similar to VmTemplate)
    public string Description { get; set; }
    public string LongDescription { get; set; }
    public string Category { get; set; }              // "web-apps", "saas-stack", "ai-pipeline"
    public List<string> Tags { get; set; }
    public string IconUrl { get; set; }
    public List<string> Screenshots { get; set; }
    
    // Author & Attribution
    public string AuthorId { get; set; }
    public string AuthorName { get; set; }
    public string License { get; set; }
    public string SourceUrl { get; set; }
    
    // Components (VM Templates in this solution)
    public List<SolutionComponent> Components { get; set; }
    
    // Network Configuration
    public SolutionNetworkConfig NetworkConfig { get; set; }
    public bool RequiresPrivateNetwork { get; set; }   // VMs need to talk to each other
    
    // Deployment Configuration
    public DeploymentStrategy Strategy { get; set; }   // Sequential, Parallel, Custom
    public int EstimatedDeploymentTimeMinutes { get; set; }
    
    // Resource Summary (aggregate of all components)
    public VmSpec TotalResources { get; set; }
    public decimal EstimatedMonthlyCost { get; set; }
    
    // Marketplace Stats (similar to VmTemplate)
    public int DeploymentCount { get; set; }
    public double AverageRating { get; set; }
    public int RatingCount { get; set; }
    
    // Status & Visibility
    public TemplateStatus Status { get; set; }
    public TemplateVisibility Visibility { get; set; }
    public bool IsFeatured { get; set; }
    
    // Timestamps
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SolutionComponent
{
    public string Name { get; set; }                  // "database", "webserver", "cache"
    public string VmTemplateId { get; set; }          // Reference to VmTemplate
    public VmSpec? CustomSpec { get; set; }           // Override template defaults
    public Dictionary<string, string> EnvironmentVariables { get; set; }
    
    // Dependencies
    public List<string> DependsOn { get; set; }       // ["database"] - deploy after these
    public int StartupDelaySeconds { get; set; }      // Wait before starting next
    
    // Networking
    public bool IsPublicFacing { get; set; }          // Has ingress domain
    public List<ServiceConnection> Connections { get; set; }  // Connects to other components
    
    // Lifecycle
    public bool IsOptional { get; set; }              // Can be skipped during deployment
    public int DeploymentOrder { get; set; }          // Explicit order (if not dependency-based)
}

public class ServiceConnection
{
    public string TargetComponent { get; set; }       // "database"
    public int TargetPort { get; set; }               // 3306
    public string Protocol { get; set; }              // "mysql", "redis", "http"
    public string EnvironmentVariable { get; set; }   // "DATABASE_URL"
}

public class SolutionNetworkConfig
{
    public bool CreatePrivateNetwork { get; set; }
    public string NetworkName { get; set; }
    public string SubnetCidr { get; set; }            // "10.100.0.0/24"
    public bool EnableDns { get; set; }               // Components can reach each other by name
}

public enum DeploymentStrategy
{
    Sequential,     // One at a time, wait for each
    Parallel,       // All at once
    DependencyBased // Use DependsOn relationships
}
```

### 3. TemplateRating

```csharp
public class TemplateRating
{
    public string Id { get; set; }
    public string TemplateId { get; set; }            // VmTemplate or SolutionTemplate
    public TemplateType TemplateType { get; set; }
    
    public string UserId { get; set; }                // Wallet address
    public string UserDisplayName { get; set; }
    
    public int Rating { get; set; }                   // 1-5 stars
    public string Review { get; set; }                // Optional text review
    
    public List<string> Tags { get; set; }            // ["easy-setup", "great-performance"]
    
    public int HelpfulVotes { get; set; }             // Upvotes from other users
    
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    
    // Verification
    public bool IsVerifiedDeployment { get; set; }    // User actually deployed it
    public string DeploymentId { get; set; }          // Proof of deployment
}

public enum TemplateType
{
    VmTemplate,
    SolutionTemplate
}
```

### 4. TemplateDeployment

```csharp
public class TemplateDeployment
{
    public string Id { get; set; }
    public string TemplateId { get; set; }
    public TemplateType TemplateType { get; set; }
    
    public string UserId { get; set; }
    
    // For VM Templates
    public string? VmId { get; set; }                 // Single VM created
    
    // For Solution Templates
    public List<string> VmIds { get; set; }           // Multiple VMs created
    public Dictionary<string, string> ComponentMapping { get; set; }  // component name -> vmId
    
    public DeploymentStatus Status { get; set; }
    public string ErrorMessage { get; set; }
    
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int DurationSeconds { get; set; }
    
    // Configuration used
    public VmSpec SpecUsed { get; set; }
    public string NodeId { get; set; }                // If user selected specific node
    
    // Cost tracking
    public decimal EstimatedCostPerHour { get; set; }
    public decimal TotalCostToDate { get; set; }
}

public enum DeploymentStatus
{
    Pending,        // Queued
    Provisioning,   // Creating VMs
    Configuring,    // Running cloud-init
    Running,        // Active
    Failed,         // Deployment failed
    Stopped,        // User stopped
    Deleted         // Cleaned up
}
```

### 5. TemplateCategory

```csharp
public class TemplateCategory
{
    public string Id { get; set; }
    public string Name { get; set; }                  // "AI & Machine Learning"
    public string Slug { get; set; }                  // "ai-ml"
    public string Description { get; set; }
    public string IconName { get; set; }              // Icon identifier
    public int DisplayOrder { get; set; }
    public int TemplateCount { get; set; }            // Cached count
}
```

---

## Template Types

### Single VM Templates

**Examples:**

1. **Stable Diffusion WebUI**
   - GPU required
   - 8 vCPU, 24GB RAM, 100GB storage
   - Pre-installed AUTOMATIC1111 WebUI
   - Access: `https://{vm-id}.decloud.app:7860`

2. **Jupyter Data Science**
   - 4 vCPU, 8GB RAM, 50GB storage
   - Python 3.11, pandas, numpy, matplotlib
   - Access: `https://{vm-id}.decloud.app:8888`

3. **VS Code Server**
   - 2 vCPU, 4GB RAM, 30GB storage
   - Code-server, Git, Node.js
   - Access: `https://{vm-id}.decloud.app:8080`

4. **Docker Host**
   - 4 vCPU, 8GB RAM, 100GB storage
   - Docker, Docker Compose, Portainer
   - Access: `https://{vm-id}.decloud.app:9000`

### Solution Templates (Multi-VM)

**Examples:**

1. **WordPress Stack**
   ```
   Components:
   ├── webserver (WordPress + Apache)
   │   ├── 2 vCPU, 4GB RAM
   │   ├── Depends on: database
   │   └── Public: https://{vm-id}.decloud.app
   ├── database (MySQL 8.0)
   │   ├── 2 vCPU, 4GB RAM
   │   └── Private network only
   └── cache (Redis)
       ├── 1 vCPU, 2GB RAM
       └── Private network only
   
   Total: 5 vCPU, 10GB RAM, ~$0.15/hour
   ```

2. **AI Inference Pipeline**
   ```
   Components:
   ├── api-gateway (Nginx + FastAPI)
   │   ├── 2 vCPU, 4GB RAM
   │   └── Public: https://{vm-id}.decloud.app
   ├── inference-worker-1 (GPU)
   │   ├── 8 vCPU, 24GB RAM, RTX 3090
   │   └── Private network
   ├── inference-worker-2 (GPU)
   │   ├── 8 vCPU, 24GB RAM, RTX 3090
   │   └── Private network
   └── queue (RabbitMQ)
       ├── 2 vCPU, 4GB RAM
       └── Private network
   
   Total: 20 vCPU, 56GB RAM, 2x GPU, ~$2.50/hour
   ```

3. **SaaS Starter Stack**
   ```
   Components:
   ├── frontend (React + Vite)
   │   ├── 1 vCPU, 2GB RAM
   │   └── Public: https://{vm-id}.decloud.app
   ├── backend (Node.js + Express)
   │   ├── 2 vCPU, 4GB RAM
   │   └── Public API: https://api-{vm-id}.decloud.app
   ├── database (PostgreSQL)
   │   ├── 2 vCPU, 8GB RAM
   │   └── Private network
   └── monitoring (Grafana + Prometheus)
       ├── 2 vCPU, 4GB RAM
       └── Public: https://monitor-{vm-id}.decloud.app
   
   Total: 7 vCPU, 18GB RAM, ~$0.25/hour
   ```

---

## Backend API Design

### Endpoints Structure

```
/api/marketplace
├── /templates
│   ├── GET  /                       # List all VM templates
│   ├── GET  /featured               # Featured templates
│   ├── GET  /trending               # Most deployed this week
│   ├── GET  /categories/{category}  # By category
│   ├── GET  /{templateId}           # Get template details
│   ├── POST /                       # Create new template
│   ├── PUT  /{templateId}           # Update template
│   ├── DELETE /{templateId}         # Delete template
│   ├── POST /{templateId}/fork      # Fork/clone template
│   ├── POST /{templateId}/deploy    # Deploy template
│   └── GET  /{templateId}/deployments # Deployment history
│
├── /solutions
│   ├── GET  /                       # List solution templates
│   ├── GET  /featured
│   ├── GET  /{solutionId}
│   ├── POST /                       # Create solution
│   ├── PUT  /{solutionId}
│   ├── POST /{solutionId}/deploy    # Deploy multi-VM solution
│   └── GET  /{solutionId}/cost-estimate
│
├── /ratings
│   ├── POST /{templateId}/rate      # Rate template
│   ├── GET  /{templateId}/ratings   # Get ratings
│   ├── PUT  /{ratingId}             # Update your rating
│   └── POST /{ratingId}/helpful     # Mark review as helpful
│
├── /categories
│   ├── GET  /                       # List categories
│   └── GET  /{categorySlug}         # Category details
│
└── /my
    ├── GET  /templates              # My created templates
    ├── GET  /solutions              # My created solutions
    ├── GET  /favorites              # Favorited templates
    ├── POST /favorites/{templateId} # Add to favorites
    └── GET  /deployments            # My deployments
```

### API Examples

#### 1. List Templates with Filtering

```http
GET /api/marketplace/templates?category=ai-ml&requiresGpu=true&minRating=4.0&sort=popular

Response 200:
{
  "success": true,
  "data": {
    "templates": [
      {
        "id": "01hsxx...",
        "name": "Stable Diffusion WebUI",
        "slug": "stable-diffusion-webui",
        "version": "1.6.0",
        "description": "AUTOMATIC1111 WebUI with all extensions",
        "category": "ai-ml",
        "tags": ["gpu", "stable-diffusion", "ai"],
        "authorName": "0xAlex",
        "averageRating": 4.8,
        "ratingCount": 245,
        "deploymentCount": 1523,
        "activeDeployments": 89,
        "requiresGpu": true,
        "recommendedSpec": {
          "virtualCpuCores": 8,
          "memoryBytes": 25769803776,
          "diskBytes": 107374182400
        },
        "estimatedCostPerHour": 0.85,
        "iconUrl": "https://...",
        "isFeatured": true,
        "isVerified": true
      }
    ],
    "totalCount": 42,
    "page": 1,
    "pageSize": 20
  }
}
```

#### 2. Get Template Details

```http
GET /api/marketplace/templates/01hsxx...

Response 200:
{
  "success": true,
  "data": {
    "id": "01hsxx...",
    "name": "Stable Diffusion WebUI",
    "longDescription": "# Stable Diffusion WebUI\n\nComplete setup with...",
    "cloudInitTemplate": "#cloud-config\npackages:\n  - python3.11...",
    "exposedPorts": [
      {
        "port": 7860,
        "protocol": "http",
        "description": "Web UI",
        "isPublic": true
      }
    ],
    "environmentVariables": {
      "COMMANDLINE_ARGS": "--xformers --opt-sdp-attention"
    },
    "screenshots": ["https://...", "https://..."],
    "sourceUrl": "https://github.com/AUTOMATIC1111/stable-diffusion-webui",
    "changelog": "https://...",
    "recentDeployments": 42,
    "topRatings": [...]
  }
}
```

#### 3. Deploy VM Template

```http
POST /api/marketplace/templates/01hsxx.../deploy

Request:
{
  "nodeId": "019542d6...",           // Optional: target specific node
  "customSpec": {                     // Optional: override defaults
    "virtualCpuCores": 12,
    "memoryBytes": 34359738368
  },
  "environmentVariables": {           // Optional: custom env vars
    "MODEL_PATH": "/models/sd-1.5"
  }
}

Response 200:
{
  "success": true,
  "data": {
    "deploymentId": "01hsyy...",
    "vmId": "01hszz...",
    "status": "provisioning",
    "estimatedCompletionMinutes": 5,
    "accessUrls": {
      "webui": "https://01hszz....decloud.app:7860",
      "ssh": "ssh://01hszz....decloud.app:22"
    },
    "credentials": {
      "password": "generated-secure-password"
    }
  }
}
```

#### 4. Deploy Solution Template

```http
POST /api/marketplace/solutions/01hsxx.../deploy

Request:
{
  "nodeId": "019542d6...",            // Optional: deploy all VMs to same node
  "componentOverrides": {             // Optional: customize components
    "database": {
      "spec": {
        "virtualCpuCores": 4,
        "memoryBytes": 8589934592
      }
    }
  },
  "skipOptionalComponents": ["monitoring"]
}

Response 200:
{
  "success": true,
  "data": {
    "deploymentId": "01hsyy...",
    "solutionId": "01hsxx...",
    "status": "provisioning",
    "components": [
      {
        "name": "webserver",
        "vmId": "01hszz1...",
        "status": "provisioning",
        "order": 2
      },
      {
        "name": "database",
        "vmId": "01hszz2...",
        "status": "provisioning",
        "order": 1
      },
      {
        "name": "cache",
        "vmId": "01hszz3...",
        "status": "pending",
        "order": 3
      }
    ],
    "estimatedCompletionMinutes": 8,
    "networkConfig": {
      "privateNetworkId": "net-01hs...",
      "subnet": "10.100.0.0/24"
    }
  }
}
```

#### 5. Create New Template

```http
POST /api/marketplace/templates

Request:
{
  "name": "My Custom AI Tool",
  "slug": "my-custom-ai-tool",
  "description": "Custom AI inference server",
  "category": "ai-ml",
  "tags": ["ai", "python", "custom"],
  "visibility": "public",
  "defaultSpec": {
    "virtualCpuCores": 4,
    "memoryBytes": 8589934592,
    "diskBytes": 53687091200,
    "qualityTier": 1
  },
  "requiresGpu": false,
  "cloudInitTemplate": "#cloud-config\npackages:\n  - python3.11\n...",
  "exposedPorts": [
    {
      "port": 8000,
      "protocol": "http",
      "description": "API Endpoint",
      "isPublic": true
    }
  ],
  "license": "MIT",
  "sourceUrl": "https://github.com/user/repo"
}

Response 201:
{
  "success": true,
  "data": {
    "id": "01hszz...",
    "slug": "my-custom-ai-tool",
    "status": "published",
    "url": "https://decloud.app/marketplace/templates/my-custom-ai-tool"
  }
}
```

---

## Frontend Design

### Page Structure

```
/marketplace
├── /                                # Marketplace home
├── /templates                       # Browse VM templates
├── /templates/:slug                 # Template detail page
├── /solutions                       # Browse solution templates
├── /solutions/:slug                 # Solution detail page
├── /categories/:category            # Category view
├── /create                          # Create new template wizard
└── /my-templates                    # User's creations & favorites
```

### Marketplace Home Layout

```
┌─────────────────────────────────────────────────────────────┐
│  🎨 DeCloud Marketplace                         [My Account]│
│  Discover, Deploy, Create                                   │
├─────────────────────────────────────────────────────────────┤
│  [ 🔍 Search templates, solutions...              ] [Filter]│
├─────────────────────────────────────────────────────────────┤
│  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐       │
│  │   🤖    │  │   🌐    │  │   🛠️    │  │   💾    │       │
│  │  AI/ML  │  │  Web    │  │  Dev    │  │  Data   │       │
│  │  (245)  │  │  (189)  │  │  (156)  │  │  (93)   │       │
│  └─────────┘  └─────────┘  └─────────┘  └─────────┘       │
├─────────────────────────────────────────────────────────────┤
│  🌟 Featured Templates                         [See All →] │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│  │ 🎨 Stable    │  │ 📊 Jupyter   │  │ 💬 LLaMA     │     │
│  │  Diffusion   │  │  Lab         │  │  Chat        │     │
│  │              │  │              │  │              │     │
│  │ ⭐ 4.8 (245) │  │ ⭐ 4.6 (156) │  │ ⭐ 4.9 (312) │     │
│  │ 🚀 1.5k uses │  │ 🚀 890 uses  │  │ 🚀 2.1k uses │     │
│  │ 💰 $0.85/hr  │  │ 💰 $0.25/hr  │  │ 💰 $1.20/hr  │     │
│  │              │  │              │  │              │     │
│  │ [🚀 Deploy]  │  │ [🚀 Deploy]  │  │ [🚀 Deploy]  │     │
│  └──────────────┘  └──────────────┘  └──────────────┘     │
├─────────────────────────────────────────────────────────────┤
│  🔥 Trending This Week                         [See All →] │
│  (Similar card layout...)                                   │
├─────────────────────────────────────────────────────────────┤
│  🏗️ Solution Templates                         [See All →] │
│  ┌──────────────┐  ┌──────────────┐                        │
│  │ 🌐 WordPress │  │ 🤖 AI        │                        │
│  │  Stack       │  │  Pipeline    │                        │
│  │  3 VMs       │  │  4 VMs       │                        │
│  │ ⭐ 4.7 (89)  │  │ ⭐ 4.8 (45)  │                        │
│  │ 💰 $0.15/hr  │  │ 💰 $2.50/hr  │                        │
│  │ [🚀 Deploy]  │  │ [🚀 Deploy]  │                        │
│  └──────────────┘  └──────────────┘                        │
└─────────────────────────────────────────────────────────────┘
```

### Template Detail Page

```
┌─────────────────────────────────────────────────────────────┐
│  ← Back to Templates                             [Edit]     │
├─────────────────────────────────────────────────────────────┤
│  ┌──────┐  Stable Diffusion WebUI    ✓ Verified           │
│  │ 🎨   │  by @0xAlex                 🌟 Featured           │
│  │      │  v1.6.0                                           │
│  └──────┘                                                   │
│                                                             │
│  ⭐⭐⭐⭐⭐ 4.8/5 (245 ratings)  🚀 1,523 deployments        │
│  [⭐ Rate]  [❤️ Favorite]  [🔀 Fork]  [🚀 Deploy Now]      │
├─────────────────────────────────────────────────────────────┤
│  📸 Screenshots                                             │
│  [Image Gallery Carousel]                                   │
├─────────────────────────────────────────────────────────────┤
│  📝 Description                                             │
│  Complete AUTOMATIC1111 Stable Diffusion WebUI with all    │
│  popular extensions pre-installed. GPU-accelerated with    │
│  xFormers optimization. Ready to generate images in 5 mins. │
│                                                             │
│  ✨ Features:                                               │
│  • ControlNet, Deforum, Dynamic Prompts                    │
│  • Model downloader with popular checkpoints              │
│  • Automatic updates on startup                           │
│                                                             │
│  🔗 [GitHub] [Documentation] [Changelog]                   │
├─────────────────────────────────────────────────────────────┤
│  💻 Requirements                                            │
│  ┌────────────────────┬────────────────────┐              │
│  │ Minimum            │ Recommended        │              │
│  ├────────────────────┼────────────────────┤              │
│  │ 4 vCPU             │ 8 vCPU             │              │
│  │ 16 GB RAM          │ 24 GB RAM          │              │
│  │ 50 GB Storage      │ 100 GB NVMe        │              │
│  │ RTX 3060           │ RTX 3090           │              │
│  │ ~$0.60/hour        │ ~$0.85/hour        │              │
│  └────────────────────┴────────────────────┘              │
│                                                             │
│  📍 Access: https://{vm-id}.decloud.app:7860               │
│  🔐 Authentication: Generated password                     │
├─────────────────────────────────────────────────────────────┤
│  🚀 Deploy Configuration                                    │
│  ┌───────────────────────────────────────┐                │
│  │ Select Deployment Mode:                │                │
│  │ ◉ Auto-select best node (Recommended)  │                │
│  │ ○ Choose specific node                 │                │
│  │                                         │                │
│  │ Resource Tier:                          │                │
│  │ [○ Minimum  ◉ Recommended  ○ Custom]   │                │
│  │                                         │                │
│  │ Estimated Cost: $0.85/hour              │                │
│  │                 ~$612/month              │                │
│  │                                         │                │
│  │ [Cancel]           [🚀 Deploy Now]     │                │
│  └───────────────────────────────────────┘                │
├─────────────────────────────────────────────────────────────┤
│  💬 Reviews (245)                                           │
│  ┌─────────────────────────────────────────────────┐      │
│  │ ⭐⭐⭐⭐⭐  @0xBob  •  2 days ago  •  Verified ✓  │      │
│  │                                                   │      │
│  │ "Amazing setup! Was generating images in under   │      │
│  │  5 minutes. All the extensions work perfectly."  │      │
│  │                                                   │      │
│  │ 👍 Helpful (42)                                  │      │
│  └─────────────────────────────────────────────────┘      │
│  [Load More Reviews...]                                    │
└─────────────────────────────────────────────────────────────┘
```

### Template Creation Wizard

```
┌─────────────────────────────────────────────────────────────┐
│  Create New Template                        [Save Draft]    │
├─────────────────────────────────────────────────────────────┤
│  ① Basic Info  →  ② Resources  →  ③ Configuration  →  ④ Publish│
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  📝 Template Information                                    │
│  ┌───────────────────────────────────────────┐            │
│  │ Name: [_____________________________]      │            │
│  │ Slug: [_____________________________]      │            │
│  │ Category: [AI & Machine Learning  ▼]       │            │
│  │ Tags: [gpu] [stable-diffusion] [+Add]      │            │
│  │                                            │            │
│  │ Short Description (280 chars):             │            │
│  │ [__________________________________]       │            │
│  │ [__________________________________]       │            │
│  │                                            │            │
│  │ Long Description (Markdown):               │            │
│  │ ┌────────────────────────────────┐        │            │
│  │ │ # My Template                  │        │            │
│  │ │                                │        │            │
│  │ │ Full description here...       │        │            │
│  │ │                                │        │            │
│  │ └────────────────────────────────┘        │            │
│  │                                            │            │
│  │ Icon/Logo: [Upload] or [Enter URL]        │            │
│  │ Screenshots: [Upload Images]               │            │
│  │                                            │            │
│  │ Source Code: [____________________]        │            │
│  │ License: [MIT                    ▼]        │            │
│  └───────────────────────────────────────────┘            │
│                                                             │
│  [← Back]                        [Continue →]              │
└─────────────────────────────────────────────────────────────┘
```

### Solution Template Detail

```
┌─────────────────────────────────────────────────────────────┐
│  WordPress Stack Solution                                   │
│  by @WebDev   ⭐ 4.7 (89)   🚀 456 deployments             │
├─────────────────────────────────────────────────────────────┤
│  🏗️ Architecture                                            │
│  ┌───────────────────────────────────────────┐             │
│  │                                           │             │
│  │  [🌐 WordPress]                          │             │
│  │     2 vCPU, 4GB                          │             │
│  │     Public: https://{id}.decloud.app     │             │
│  │          ↓                                │             │
│  │     [💾 MySQL]    [⚡ Redis]             │             │
│  │     2 vCPU, 4GB   1 vCPU, 2GB            │             │
│  │     Private       Private                 │             │
│  │                                           │             │
│  └───────────────────────────────────────────┘             │
│                                                             │
│  📊 Total Resources                                         │
│  • 5 vCPU cores                                            │
│  • 10 GB RAM                                               │
│  • 60 GB Storage                                           │
│  • ~$0.15/hour (~$108/month)                              │
│  • Deploy time: ~6 minutes                                │
│                                                             │
│  🔗 Components                                              │
│  ┌─────────────────────────────────────┐                  │
│  │ webserver (WordPress 6.4)            │                  │
│  │ • Depends on: database, cache        │                  │
│  │ • Public access enabled              │                  │
│  │ [View Template →]                   │                  │
│  └─────────────────────────────────────┘                  │
│  ┌─────────────────────────────────────┐                  │
│  │ database (MySQL 8.0)                 │                  │
│  │ • Auto-configured for WordPress      │                  │
│  │ • Daily backups included             │                  │
│  │ [View Template →]                   │                  │
│  └─────────────────────────────────────┘                  │
│  ┌─────────────────────────────────────┐                  │
│  │ cache (Redis 7.2)            [Optional]│                  │
│  │ • Improves site performance          │                  │
│  │ [View Template →]                   │                  │
│  └─────────────────────────────────────┘                  │
│                                                             │
│  [🚀 Deploy Solution]                                      │
└─────────────────────────────────────────────────────────────┘
```

---

## User Flows

### Flow 1: Browse → Deploy VM Template

```
1. User visits /marketplace
   ↓
2. Sees featured templates, browses categories
   ↓
3. Clicks on "Stable Diffusion WebUI" card
   ↓
4. Reviews template details:
   - Screenshots
   - Requirements (GPU needed)
   - Cost estimate ($0.85/hour)
   - Reviews (4.8/5 stars)
   ↓
5. Clicks "🚀 Deploy Now"
   ↓
6. Configure deployment:
   - Choose resource tier (Min/Recommended/Custom)
   - [Optional] Select specific node
   - Review cost
   ↓
7. Click "Deploy"
   ↓
8. System:
   a. Creates VM with template spec
   b. Applies cloud-init configuration
   c. Waits for ready status
   ↓
9. Shows progress:
   "⏳ Provisioning VM... (Step 1/3)"
   "⚙️ Installing software... (Step 2/3)"
   "🚀 Starting services... (Step 3/3)"
   ↓
10. Deployment complete!
    Shows:
    - Access URL: https://01hszz....decloud.app:7860
    - Password: generated-secure-123
    - SSH access details
    - Cost tracking
    ↓
11. User clicks URL, starts generating images immediately
```

### Flow 2: Browse → Deploy Solution Template

```
1. User visits /marketplace/solutions
   ↓
2. Browses "WordPress Stack" solution
   ↓
3. Reviews architecture diagram
   - 3 VMs: webserver, database, cache
   - Private network between components
   - Total cost: $0.15/hour
   ↓
4. Clicks "🚀 Deploy Solution"
   ↓
5. Configure solution:
   - Can customize each component's resources
   - Can skip optional components (cache)
   - Can select node affinity (all on same node vs distributed)
   ↓
6. Click "Deploy"
   ↓
7. System orchestrates deployment:
   a. Creates private network
   b. Deploys database VM first (dependency)
   c. Waits for database ready
   d. Deploys cache VM (parallel)
   e. Deploys webserver VM last
   f. Configures environment variables
   g. Tests connectivity between components
   ↓
8. Shows real-time progress:
   "✅ database - Running"
   "✅ cache - Running"
   "⏳ webserver - Installing WordPress..."
   ↓
9. Solution ready!
   - WordPress URL: https://01hszz....decloud.app
   - Admin panel: https://01hszz....decloud.app/wp-admin
   - Database accessible from webserver via internal DNS
   - All credentials displayed
```

### Flow 3: Create & Share Template

```
1. User clicks "Create Template"
   ↓
2. Wizard Step 1: Basic Info
   - Name, description, category
   - Upload icon/screenshots
   ↓
3. Step 2: Resources
   - Set min/recommended specs
   - Specify GPU requirements
   - Define exposed ports
   ↓
4. Step 3: Configuration
   - Write cloud-init script or paste existing
   - Define environment variables
   - Test configuration (optional)
   ↓
5. Step 4: Publish
   - Choose visibility (Public/Unlisted/Private)
   - Add source code URL
   - Select license
   ↓
6. Click "Publish Template"
   ↓
7. Template goes live at:
   /marketplace/templates/{user-slug}/{template-slug}
   ↓
8. User can:
   - Share link
   - See deployment analytics
   - Receive feedback/ratings
   - Update/version template
   ↓
9. Community discovers & deploys
   ↓
10. Creator can:
    - Track deployments
    - See ratings/reviews
    - Earn reputation
    - [Future] Earn revenue share
```

### Flow 4: Fork & Customize Template

```
1. User finds template they like but wants to modify
   ↓
2. Clicks "🔀 Fork"
   ↓
3. System creates copy:
   - Copies all configuration
   - Links to parent template
   - User becomes owner of fork
   ↓
4. User edits forked template:
   - Modify cloud-init script
   - Change resource requirements
   - Update description
   ↓
5. Publishes as new template
   ↓
6. Fork is visible on original template:
   "42 forks" with links to variants
```

---

## Cloud-Init Integration

### Template Format

```yaml
#cloud-config

# Basic system setup
timezone: UTC
package_update: true
package_upgrade: true

# Install packages
packages:
  - python3.11
  - python3-pip
  - git
  - curl
  - nvidia-cuda-toolkit  # If GPU template

# Create user
users:
  - name: app
    sudo: ALL=(ALL) NOPASSWD:ALL
    shell: /bin/bash
    groups: docker

# Write files
write_files:
  - path: /etc/systemd/system/app.service
    content: |
      [Unit]
      Description=Application Service
      After=network.target
      
      [Service]
      Type=simple
      User=app
      WorkingDirectory=/opt/app
      ExecStart=/opt/app/start.sh
      Restart=always
      
      [Install]
      WantedBy=multi-user.target

# Run commands
runcmd:
  # Clone application
  - git clone https://github.com/user/app /opt/app
  - chown -R app:app /opt/app
  
  # Install dependencies
  - cd /opt/app && pip3 install -r requirements.txt
  
  # Configure application
  - |
    cat > /opt/app/.env <<EOF
    PORT=8000
    DATABASE_URL=${DATABASE_URL}
    API_KEY=${API_KEY}
    EOF
  
  # Start service
  - systemctl daemon-reload
  - systemctl enable app.service
  - systemctl start app.service
  
  # Wait for ready
  - |
    for i in {1..30}; do
      curl -s http://localhost:8000/health && break
      sleep 2
    done

# Final message
final_message: |
  Application is ready!
  Access at: https://{DECLOUD_VM_ID}.decloud.app:8000
```

### Variable Substitution

The system supports variable substitution in cloud-init templates:

```yaml
runcmd:
  - echo "VM ID: ${DECLOUD_VM_ID}"
  - echo "VM Name: ${DECLOUD_VM_NAME}"
  - echo "Domain: ${DECLOUD_DOMAIN}"
  - echo "SSH Password: ${DECLOUD_PASSWORD}"
  - echo "Node Region: ${DECLOUD_NODE_REGION}"
  
  # For solution templates
  - echo "Database URL: ${DATABASE_PRIVATE_IP}:3306"
  - echo "Cache URL: ${CACHE_PRIVATE_IP}:6379"
```

### Multi-VM Solution Cloud-Init

For solution templates, components can reference each other:

```yaml
# webserver component cloud-init
#cloud-config
runcmd:
  - |
    cat > /opt/wordpress/wp-config.php <<EOF
    define('DB_HOST', '${COMPONENT_DATABASE_PRIVATE_IP}:3306');
    define('DB_NAME', 'wordpress');
    define('DB_USER', 'wp_user');
    define('DB_PASSWORD', '${DATABASE_PASSWORD}');
    
    define('WP_REDIS_HOST', '${COMPONENT_CACHE_PRIVATE_IP}');
    define('WP_REDIS_PORT', 6379);
    EOF
```

---

## Community Features

### 1. Ratings & Reviews

**Features:**
- 1-5 star rating system
- Written reviews with markdown support
- "Helpful" voting on reviews
- Verified deployment badge (can only review if deployed)
- Response from template author

**UI:**
```
⭐⭐⭐⭐⭐  @0xBob  •  2 days ago  •  ✓ Verified Deployment

"Amazing setup! Was generating images in under 5 minutes. 
All the extensions work perfectly. Highly recommend!"

👍 Helpful (42)  👎 Not Helpful (2)  💬 Reply

  ↪️ Author Response (@0xAlex):
     "Thanks! Glad you're enjoying it. Check out the new 
      ControlNet features in v1.7!"
```

### 2. Featured & Verified Badges

**Verified Badge (✓):**
- Template tested by DeCloud team
- Code reviewed for security
- Actively maintained
- Works as described

**Featured Badge (🌟):**
- Curated by platform
- Exceptional quality
- High user satisfaction
- Popular/trending

**Community Pick (👥):**
- Voted by community
- High deployment rate
- Strong reviews
- Active maintenance

### 3. Template Collections

Users can create collections:

```
"My AI Stack" by @0xAlex
├── Stable Diffusion WebUI
├── ComfyUI
├── LLaMA Chat
└── Whisper Transcription

32 followers  •  156 total deployments
```

### 4. Template Leaderboard

**Most Popular This Week:**
1. Stable Diffusion WebUI - 423 deployments
2. LLaMA Chat - 312 deployments
3. Jupyter Lab - 289 deployments

**Top Rated All Time:**
1. VS Code Server - 4.9⭐ (502 reviews)
2. Docker Host - 4.9⭐ (445 reviews)
3. LLaMA Chat - 4.9⭐ (312 reviews)

**Rising Stars:**
1. Whisper API - 156 deployments (↑ 245%)
2. ComfyUI - 89 deployments (↑ 189%)

### 5. Template Analytics (For Creators)

Dashboard showing:
- Total deployments over time
- Active deployments
- Average deployment duration
- Top deploying regions
- Revenue (if monetized)
- User feedback trends
- Fork tree visualization

---

## Security & Validation

### Template Validation

Before publishing, templates are validated:

```csharp
public class TemplateValidator
{
    public ValidationResult Validate(VmTemplate template)
    {
        var errors = new List<string>();
        
        // Cloud-init validation
        if (!ValidateCloudInit(template.CloudInitTemplate))
            errors.Add("Invalid cloud-init syntax");
        
        // Resource limits
        if (template.DefaultSpec.VirtualCpuCores > 32)
            errors.Add("CPU cores exceed platform limit (32)");
        
        if (template.DefaultSpec.MemoryBytes > 137438953472) // 128GB
            errors.Add("Memory exceeds platform limit (128GB)");
        
        // Security checks
        if (ContainsSuspiciousCommands(template.CloudInitTemplate))
            errors.Add("Cloud-init contains potentially dangerous commands");
        
        // Port validation
        foreach (var port in template.ExposedPorts)
        {
            if (port.Port < 1 || port.Port > 65535)
                errors.Add($"Invalid port: {port.Port}");
        }
        
        return new ValidationResult 
        { 
            IsValid = errors.Count == 0,
            Errors = errors 
        };
    }
    
    private bool ContainsSuspiciousCommands(string cloudInit)
    {
        var suspicious = new[] 
        {
            "rm -rf /",
            "dd if=/dev/zero",
            ":(){ :|:& };:",  // Fork bomb
            // etc.
        };
        
        return suspicious.Any(cmd => cloudInit.Contains(cmd));
    }
}
```

### Sandboxing & Security

**Cloud-Init Restrictions:**
- No access to host filesystem outside VM
- Cannot modify node agent
- Rate limiting on network operations
- Resource usage monitoring

**Template Reporting:**
- Users can flag malicious templates
- Automated scanning for:
  - Cryptocurrency miners
  - Botnet code
  - Data exfiltration
  - Backdoors

**Author Reputation:**
- Track template quality
- Warning for low-rated templates
- Suspension for violations
- Verified author program

---

## Implementation Phases

### Phase 1: MVP - VM Templates (Week 1)

**Backend:**
- [ ] `VmTemplate` model & MongoDB collection
- [ ] `TemplateCategory` model & seed data
- [ ] Templates API (GET, POST, PUT, DELETE)
- [ ] Deploy from template endpoint
- [ ] Cloud-init variable substitution

**Frontend:**
- [ ] Marketplace home page
- [ ] Template browsing (grid view)
- [ ] Template detail page
- [ ] Deploy from template modal
- [ ] Basic filtering (category, tags)

**Testing:**
- [ ] Create 5-10 curated templates
- [ ] End-to-end deployment testing
- [ ] Documentation

### Phase 2: Community Features (Week 2)

**Backend:**
- [ ] `TemplateRating` model & API
- [ ] `TemplateDeployment` tracking
- [ ] Featured/trending algorithms
- [ ] User favorites

**Frontend:**
- [ ] Rating & review system
- [ ] Template statistics
- [ ] User profile with templates
- [ ] Featured/trending sections

### Phase 3: Template Creation (Week 3)

**Backend:**
- [ ] Template validation service
- [ ] Fork/clone functionality
- [ ] Version management
- [ ] Template analytics

**Frontend:**
- [ ] Template creation wizard
- [ ] Cloud-init editor with validation
- [ ] Fork template UI
- [ ] Creator dashboard

### Phase 4: Solution Templates (Week 4)

**Backend:**
- [ ] `SolutionTemplate` model
- [ ] Multi-VM orchestration service
- [ ] Private network creation
- [ ] Dependency management
- [ ] Health checking & rollback

**Frontend:**
- [ ] Solution browsing
- [ ] Architecture visualization
- [ ] Component customization
- [ ] Deployment progress tracking

### Phase 5: Advanced Features (Week 5+)

**Backend:**
- [ ] Template collections
- [ ] Advanced search (AI-powered?)
- [ ] Cost estimation improvements
- [ ] Template monetization framework

**Frontend:**
- [ ] Collection management
- [ ] Advanced filters & search
- [ ] Template comparison
- [ ] Community leaderboards

---

## Success Criteria

### Metrics to Track

**Adoption:**
- Templates deployed per week (target: 100+ by month 2)
- Active templates (target: 50+ quality templates by month 3)
- Community contributions (target: 20+ user-created templates by month 3)

**Engagement:**
- Average time from browse to deploy (target: < 2 minutes)
- Template re-use rate (target: 60%+ users deploy from template)
- Review participation (target: 20%+ deployers leave review)

**Quality:**
- Average template rating (target: 4.0+ stars)
- Deployment success rate (target: 95%+)
- Template update frequency (active maintenance)

### Business Impact

**User Experience:**
- 10x faster onboarding (from hours to minutes)
- Lower technical barrier (no cloud-init knowledge needed)
- Increased confidence (reviews & verified badges)

**Platform Growth:**
- Network effects (users create → others discover → more create)
- Competitive moat (unique feature in decentralized compute)
- Community formation (template creators become advocates)

**Revenue Potential:**
- Template author fees (future)
- Premium template marketplace tier
- Enterprise template support

---

## Conclusion

This marketplace system transforms DeCloud from "decentralized VMs" into "**the platform where anyone can deploy and share compute solutions**". 

The key innovation is **composability** - just like Minecraft where simple blocks create complex worlds, simple VM templates can combine into sophisticated multi-VM solutions.

**Next Steps:**
1. Review this design
2. Prioritize which phases to implement
3. Start with Phase 1 MVP (VM Templates)
4. Iterate based on user feedback

---

**Questions to Consider:**

1. Should we start with curated templates only, or allow immediate community submissions?
2. What categories are most important for launch? (AI/ML, Web Apps, Dev Tools, Data/DB?)
3. Should solution templates be Phase 1 or can they wait?
4. Do we need template monetization from day 1 or defer?
5. How do we prevent spam/low-quality templates?
6. Should templates be immutable (versioned) or editable?

Let me know what you think! 🚀
