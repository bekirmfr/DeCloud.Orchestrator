# ============================================
# Stage 1: Build DHT node binary (Go)
# ============================================
FROM golang:1.23-alpine AS dht-build
WORKDIR /dht-node
COPY dht-node/go.mod dht-node/go.sum* ./
RUN go mod download 2>/dev/null || true
COPY dht-node/ .
RUN go mod tidy
# Cross-compile for both architectures and base64-encode
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags="-s -w" -o dht-node-amd64 . \
    && base64 -w0 dht-node-amd64 > dht-node-amd64.b64 \
    && rm dht-node-amd64
RUN CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath -ldflags="-s -w" -o dht-node-arm64 . \
    && base64 -w0 dht-node-arm64 > dht-node-arm64.b64 \
    && rm dht-node-arm64

# ============================================
# Stage 2: Build .NET Orchestrator
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY src/Orchestrator/Orchestrator.csproj ./Orchestrator/
COPY src/Shared/ ./Shared/
RUN dotnet restore Orchestrator/Orchestrator.csproj

# Copy everything else and build
COPY src/Orchestrator/ ./Orchestrator/
WORKDIR /src/Orchestrator
RUN dotnet build -c Release -o /app/build
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ============================================
# Stage 3: Runtime
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r orchestrator && useradd -r -g orchestrator orchestrator

COPY --from=build /app/publish .

# Copy pre-built DHT binaries (base64-encoded) for cloud-init embedding
COPY --from=dht-build /dht-node/dht-node-amd64.b64 ./dht-node/
COPY --from=dht-build /dht-node/dht-node-arm64.b64 ./dht-node/

# Set environment
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5000

EXPOSE 5000

USER orchestrator
ENTRYPOINT ["dotnet", "Orchestrator.dll"]
