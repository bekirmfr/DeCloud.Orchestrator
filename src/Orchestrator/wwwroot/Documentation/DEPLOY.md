# Production Deployment Guide

Complete guide for deploying DeCloud Orchestrator frontend with npm/Vite setup to production environments.

---

## 📋 Pre-Deployment Checklist

Before deploying to production:

- [ ] **Environment Variables**
  - [ ] `VITE_WALLETCONNECT_PROJECT_ID` configured in `.env`
  - [ ] Project ID registered at https://cloud.reown.com
  - [ ] Production domain added to allowed domains in WalletConnect dashboard

- [ ] **Security**
  - [ ] HTTPS certificate obtained (Let's Encrypt recommended)
  - [ ] CORS configured in backend for production domain
  - [ ] CSP headers configured
  - [ ] Rate limiting enabled on auth endpoints

- [ ] **Testing**
  - [ ] Desktop wallet connection tested (MetaMask, Coinbase)
  - [ ] Mobile wallet connection tested (WalletConnect QR code)
  - [ ] All CRUD operations work (Create VM, Delete VM, SSH keys)
  - [ ] Session restoration works after page refresh
  - [ ] Password encryption/decryption works

- [ ] **Build**
  - [ ] Production build completes: `npm run build`
  - [ ] Build size acceptable (<500 KB total)
  - [ ] No console errors in production build

---

## 🚀 Deployment Scenarios

### Scenario 1: Linux Server with Systemd (Recommended)

**Best for:** Production deployments, VPS, dedicated servers

#### Prerequisites

```bash
# Ubuntu/Debian
sudo apt-get update
sudo apt-get install -y curl wget git

# Install Node.js 20 LTS
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt-get install -y nodejs

# Install .NET 8 SDK
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0

# Install Nginx
sudo apt-get install -y nginx certbot python3-certbot-nginx
```

#### Step 1: Build Application

```bash
# Clone or upload your code
cd /opt
sudo git clone https://github.com/yourusername/decloud.git
cd decloud/src/Orchestrator/wwwroot

# Install dependencies
sudo npm install

# Create production environment file
sudo cp .env.example .env
sudo nano .env  # Add VITE_WALLETCONNECT_PROJECT_ID

# Build frontend
sudo npm run build

# Build backend
cd ../
sudo dotnet publish -c Release -o /opt/decloud-app
```

#### Step 2: Create System User

```bash
# Create dedicated user
sudo useradd -r -s /bin/false decloud

# Set permissions
sudo chown -R decloud:decloud /opt/decloud-app
```

#### Step 3: Create Systemd Service

```bash
sudo nano /etc/systemd/system/decloud-orchestrator.service
```

**Service file content:**

```ini
[Unit]
Description=DeCloud Orchestrator API
After=network.target
Documentation=https://github.com/yourusername/decloud

[Service]
Type=notify
User=decloud
Group=decloud
WorkingDirectory=/opt/decloud-app

# Security hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/decloud-app/data

# Environment
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:5050

# Execution
ExecStart=/usr/bin/dotnet /opt/decloud-app/Orchestrator.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=decloud-orchestrator

# Limits
LimitNOFILE=65536
LimitNPROC=4096

[Install]
WantedBy=multi-user.target
```

**Enable and start service:**

```bash
sudo systemctl daemon-reload
sudo systemctl enable decloud-orchestrator
sudo systemctl start decloud-orchestrator

# Check status
sudo systemctl status decloud-orchestrator

# View logs
sudo journalctl -u decloud-orchestrator -f
```

#### Step 4: Configure Nginx with SSL

```bash
sudo nano /etc/nginx/sites-available/decloud
```

**Nginx configuration:**

```nginx
# HTTP - Redirect to HTTPS
server {
    listen 80;
    listen [::]:80;
    server_name decloud.example.com;
    
    # Let's Encrypt ACME challenge
    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }
    
    # Redirect everything else to HTTPS
    location / {
        return 301 https://$server_name$request_uri;
    }
}

# HTTPS
server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name decloud.example.com;

    # SSL Configuration
    ssl_certificate /etc/letsencrypt/live/decloud.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/decloud.example.com/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_ciphers HIGH:!aNULL:!MD5;
    ssl_prefer_server_ciphers on;
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 10m;

    # Security Headers
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains; preload" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-XSS-Protection "1; mode=block" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;
    
    # Content Security Policy
    add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net https://unpkg.com; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: https:; connect-src 'self' https://rpc.walletconnect.com wss://relay.walletconnect.com https://relay.walletconnect.com https://*.walletconnect.com https://*.reown.com;" always;

    # Logging
    access_log /var/log/nginx/decloud-access.log;
    error_log /var/log/nginx/decloud-error.log;

    # Root and index
    root /opt/decloud-app/wwwroot/dist;
    index index.html;

    # Gzip compression
    gzip on;
    gzip_vary on;
    gzip_proxied any;
    gzip_comp_level 6;
    gzip_types text/plain text/css text/xml text/javascript application/json application/javascript application/xml+rss application/rss+xml font/truetype font/opentype application/vnd.ms-fontobject image/svg+xml;

    # Frontend - Serve static files
    location / {
        try_files $uri $uri/ /index.html;
        
        # Cache static assets
        location ~* \.(js|css|png|jpg|jpeg|gif|ico|svg|woff|woff2|ttf|eot)$ {
            expires 1y;
            add_header Cache-Control "public, immutable";
        }
    }

    # API - Proxy to backend
    location /api/ {
        proxy_pass http://127.0.0.1:5050;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
        
        # Timeouts
        proxy_connect_timeout 60s;
        proxy_send_timeout 60s;
        proxy_read_timeout 60s;
        
        # Buffer settings
        proxy_buffering off;
        proxy_request_buffering off;
    }

    # WebSocket - Terminal support
    location /ws/ {
        proxy_pass http://127.0.0.1:5050;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        
        # WebSocket timeouts
        proxy_connect_timeout 7d;
        proxy_send_timeout 7d;
        proxy_read_timeout 7d;
    }

    # Health check endpoint
    location /health {
        proxy_pass http://127.0.0.1:5050/health;
        access_log off;
    }
}
```

**Enable site and obtain SSL certificate:**

```bash
# Enable site
sudo ln -s /etc/nginx/sites-available/decloud /etc/nginx/sites-enabled/

# Test configuration
sudo nginx -t

# Obtain SSL certificate
sudo certbot --nginx -d decloud.example.com

# Reload Nginx
sudo systemctl reload nginx
```

#### Step 5: Setup Auto-Renewal for SSL

```bash
# Test renewal
sudo certbot renew --dry-run

# Certbot automatically creates a renewal cron job
# Verify it exists:
sudo systemctl list-timers | grep certbot
```

#### Step 6: Configure Firewall

```bash
# UFW (Ubuntu Firewall)
sudo ufw allow 'Nginx Full'
sudo ufw allow OpenSSH
sudo ufw enable
sudo ufw status
```

#### Step 7: Monitoring Setup

**Install monitoring tools:**

```bash
# Install monitoring
sudo apt-get install -y prometheus-node-exporter

# Check logs
sudo journalctl -u decloud-orchestrator -f --since "1 hour ago"
```

**Create health check script:**

```bash
sudo nano /usr/local/bin/decloud-health-check.sh
```

```bash
#!/bin/bash
HEALTH_URL="https://decloud.example.com/health"
RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" $HEALTH_URL)

if [ "$RESPONSE" != "200" ]; then
    echo "Health check failed: HTTP $RESPONSE"
    systemctl restart decloud-orchestrator
    # Send alert (optional)
    # curl -X POST "https://your-webhook-url" -d "DeCloud health check failed"
fi
```

```bash
sudo chmod +x /usr/local/bin/decloud-health-check.sh

# Add to crontab (every 5 minutes)
(crontab -l 2>/dev/null; echo "*/5 * * * * /usr/local/bin/decloud-health-check.sh") | crontab -
```

---

### Scenario 2: Docker with Docker Compose

**Best for:** Containerized deployments, development environments, cloud platforms

#### Step 1: Create Dockerfile

`src/Orchestrator/Dockerfile`:

```dockerfile
# ============================================
# Stage 1: Build Frontend
# ============================================
FROM node:20-alpine AS frontend-build

WORKDIR /app/wwwroot

# Copy package files
COPY wwwroot/package*.json ./

# Install dependencies
RUN npm ci --only=production

# Copy source
COPY wwwroot/ ./

# Build production bundle
RUN npm run build

# ============================================
# Stage 2: Build .NET Backend
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build

WORKDIR /app

# Copy csproj and restore
COPY *.csproj ./
RUN dotnet restore

# Copy source
COPY . ./

# Copy frontend build
COPY --from=frontend-build /app/wwwroot/dist ./wwwroot/dist

# Publish
RUN dotnet publish -c Release -o out

# ============================================
# Stage 3: Runtime
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy published app
COPY --from=backend-build /app/out .

# Create non-root user
RUN useradd -m -s /bin/bash decloud && \
    chown -R decloud:decloud /app

USER decloud

# Expose port
EXPOSE 5050

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:5050/health || exit 1

# Start application
ENTRYPOINT ["dotnet", "Orchestrator.dll"]
```

#### Step 2: Create docker-compose.yml

`src/Orchestrator/docker-compose.yml`:

```yaml
version: '3.8'

services:
  orchestrator:
    build:
      context: .
      dockerfile: Dockerfile
    container_name: decloud-orchestrator
    ports:
      - "5050:5050"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:5050
      - ConnectionStrings__MongoDB=mongodb://mongo:27017/decloud
    depends_on:
      - mongo
    networks:
      - decloud-network
    restart: unless-stopped
    volumes:
      - app-data:/app/data
    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"

  mongo:
    image: mongo:7
    container_name: decloud-mongo
    ports:
      - "27017:27017"
    environment:
      - MONGO_INITDB_ROOT_USERNAME=admin
      - MONGO_INITDB_ROOT_PASSWORD=changeme
      - MONGO_INITDB_DATABASE=decloud
    networks:
      - decloud-network
    restart: unless-stopped
    volumes:
      - mongo-data:/data/db
      - mongo-config:/data/configdb

  nginx:
    image: nginx:alpine
    container_name: decloud-nginx
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
      - ./ssl:/etc/nginx/ssl:ro
      - certbot-www:/var/www/certbot:ro
      - certbot-conf:/etc/letsencrypt:ro
    depends_on:
      - orchestrator
    networks:
      - decloud-network
    restart: unless-stopped

  certbot:
    image: certbot/certbot:latest
    container_name: decloud-certbot
    volumes:
      - certbot-www:/var/www/certbot:rw
      - certbot-conf:/etc/letsencrypt:rw
    entrypoint: "/bin/sh -c 'trap exit TERM; while :; do certbot renew; sleep 12h & wait $${!}; done;'"

volumes:
  app-data:
  mongo-data:
  mongo-config:
  certbot-www:
  certbot-conf:

networks:
  decloud-network:
    driver: bridge
```

#### Step 3: Deploy with Docker Compose

```bash
# Build and start services
docker-compose up -d --build

# Check logs
docker-compose logs -f orchestrator

# Check health
curl http://localhost:5050/health

# Stop services
docker-compose down

# Stop and remove volumes (WARNING: deletes data)
docker-compose down -v
```

---

### Scenario 3: Azure App Service

**Best for:** Cloud deployments, scalability, managed services

#### Prerequisites

```bash
# Install Azure CLI
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash

# Login
az login
```

#### Step 1: Create Resources

```bash
# Variables
RESOURCE_GROUP="decloud-rg"
LOCATION="eastus"
APP_SERVICE_PLAN="decloud-plan"
APP_NAME="decloud-orchestrator"

# Create resource group
az group create --name $RESOURCE_GROUP --location $LOCATION

# Create App Service plan (Linux, .NET 8)
az appservice plan create \
    --name $APP_SERVICE_PLAN \
    --resource-group $RESOURCE_GROUP \
    --location $LOCATION \
    --sku B1 \
    --is-linux

# Create web app
az webapp create \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --plan $APP_SERVICE_PLAN \
    --runtime "DOTNETCORE:8.0"
```

#### Step 2: Configure App Settings

```bash
# Set environment variables
az webapp config appsettings set \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --settings \
        ASPNETCORE_ENVIRONMENT="Production" \
        VITE_WALLETCONNECT_PROJECT_ID="your-project-id"

# Enable HTTPS only
az webapp update \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --https-only true
```

#### Step 3: Deploy Application

```bash
# Build locally
cd src/Orchestrator/wwwroot
npm install
npm run build

cd ../
dotnet publish -c Release -o ./publish

# Create deployment package
cd publish
zip -r ../deploy.zip .
cd ..

# Deploy to Azure
az webapp deployment source config-zip \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --src deploy.zip

# Check deployment status
az webapp log tail --name $APP_NAME --resource-group $RESOURCE_GROUP
```

#### Step 4: Configure Custom Domain (Optional)

```bash
# Add custom domain
az webapp config hostname add \
    --webapp-name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --hostname decloud.example.com

# Enable SSL
az webapp config ssl bind \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --certificate-thumbprint <thumbprint> \
    --ssl-type SNI
```

---

## 🔍 Post-Deployment Verification

After deployment, verify everything works:

### 1. Health Check

```bash
curl https://decloud.example.com/health
# Expected: {"status":"healthy","timestamp":"..."}
```

### 2. Frontend Loading

```bash
curl -I https://decloud.example.com
# Expected: HTTP/2 200
```

### 3. API Endpoint

```bash
curl https://decloud.example.com/api/auth/message?walletAddress=0x123...
# Expected: JSON with message and timestamp
```

### 4. Wallet Connection

- Open https://decloud.example.com
- Click "Connect Wallet"
- Test MetaMask (desktop)
- Test WalletConnect (mobile)
- Verify authentication succeeds

### 5. Full Workflow

- Login with wallet
- Create a VM
- View VMs list
- Reveal VM password
- Add SSH key
- Delete SSH key
- Disconnect wallet
- Verify session restoration works

---

## 📊 Monitoring & Maintenance

### Log Monitoring

```bash
# Systemd service logs
sudo journalctl -u decloud-orchestrator -f

# Nginx access logs
sudo tail -f /var/log/nginx/decloud-access.log

# Nginx error logs
sudo tail -f /var/log/nginx/decloud-error.log

# Docker logs
docker-compose logs -f orchestrator
```

### Performance Monitoring

**Setup Prometheus + Grafana (optional):**

```bash
# Add to docker-compose.yml
prometheus:
  image: prom/prometheus
  volumes:
    - ./prometheus.yml:/etc/prometheus/prometheus.yml
  ports:
    - "9090:9090"

grafana:
  image: grafana/grafana
  ports:
    - "3000:3000"
  environment:
    - GF_SECURITY_ADMIN_PASSWORD=changeme
```

### Backup Strategy

```bash
# Backup MongoDB
docker exec decloud-mongo mongodump --out /backup

# Backup application data
sudo tar -czf decloud-backup-$(date +%Y%m%d).tar.gz /opt/decloud-app/data

# Automated daily backups
echo "0 2 * * * /usr/local/bin/decloud-backup.sh" | sudo crontab -
```

---

## 🚨 Troubleshooting Production Issues

### Issue: 502 Bad Gateway

**Diagnosis:**
```bash
# Check backend status
sudo systemctl status decloud-orchestrator

# Check backend logs
sudo journalctl -u decloud-orchestrator --since "10 minutes ago"

# Check if port is listening
sudo netstat -tlnp | grep 5050
```

**Solution:**
```bash
# Restart backend
sudo systemctl restart decloud-orchestrator

# Check Nginx proxy settings
sudo nginx -t
sudo systemctl reload nginx
```

### Issue: Wallet Connection Fails

**Diagnosis:**
- Check browser console for errors
- Verify WalletConnect Project ID
- Check HTTPS is enabled
- Verify domain in WalletConnect dashboard

**Solution:**
```bash
# Verify environment variable
sudo grep WALLETCONNECT /opt/decloud-app/wwwroot/dist/assets/*.js

# Check CORS headers
curl -I https://decloud.example.com/api/auth/message
```

### Issue: High Memory Usage

**Diagnosis:**
```bash
# Check memory usage
free -h
top -p $(pgrep -f Orchestrator.dll)

# Check for memory leaks
dotnet-counters monitor --process-id $(pgrep -f Orchestrator.dll)
```

**Solution:**
```bash
# Restart service
sudo systemctl restart decloud-orchestrator

# Adjust systemd limits
sudo nano /etc/systemd/system/decloud-orchestrator.service
# Add: MemoryLimit=512M
```

---

## 📞 Support & Resources

- **Documentation:** README.md
- **Issue Tracker:** GitHub Issues
- **Health Endpoint:** /health
- **Logs:** /var/log/nginx/, journalctl

**Emergency Contacts:**
- DevOps: devops@example.com
- Support: support@example.com

---

## ✅ Deployment Checklist

- [ ] Pre-deployment checks completed
- [ ] Build successful (`npm run build`)
- [ ] Environment variables configured
- [ ] SSL certificate obtained
- [ ] Nginx configured with security headers
- [ ] Systemd service created and enabled
- [ ] Firewall configured
- [ ] Health checks passing
- [ ] Wallet connection tested (desktop + mobile)
- [ ] All features tested in production
- [ ] Monitoring setup complete
- [ ] Backup strategy implemented
- [ ] Documentation updated

**Your deployment is complete!** 🎉
