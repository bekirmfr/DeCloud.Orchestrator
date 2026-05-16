# ============================================
# Stage 1: Build .NET Orchestrator
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files first so restore is cached independently of source changes.
COPY src/Orchestrator/Orchestrator.csproj ./Orchestrator/
COPY src/DeCloud.Shared/DeCloud.Shared.csproj ./DeCloud.Shared/
RUN dotnet restore Orchestrator/Orchestrator.csproj

# Copy source and build.
COPY src/Orchestrator/ ./Orchestrator/
COPY src/DeCloud.Shared/ ./DeCloud.Shared/
WORKDIR /src/Orchestrator
RUN dotnet build -c Release -o /app/build
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ============================================
# Stage 2: Runtime
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create non-root user
# cosign is required by BinaryReleaseResolver to verify system VM binary manifests.
# Pinned to the same version used in CI and the node agent install.sh.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && curl -fsSL https://github.com/sigstore/cosign/releases/download/v2.4.1/cosign-linux-amd64 \
       -o /usr/local/bin/cosign \
    && chmod +x /usr/local/bin/cosign \
    && apt-get purge -y curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd -r orchestrator && useradd -r -g orchestrator orchestrator

COPY --from=build /app/publish .

# Set environment
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5000

EXPOSE 5000

USER orchestrator
ENTRYPOINT ["dotnet", "Orchestrator.dll"]
