# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY src/Orchestrator/Orchestrator.csproj ./Orchestrator/
RUN dotnet restore Orchestrator/Orchestrator.csproj

# Copy everything else and build
COPY src/Orchestrator/ ./Orchestrator/
WORKDIR /src/Orchestrator
RUN dotnet build -c Release -o /app/build
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create non-root user
RUN groupadd -r orchestrator && useradd -r -g orchestrator orchestrator

COPY --from=build /app/publish .

# Set environment
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5000

EXPOSE 5000

USER orchestrator
ENTRYPOINT ["dotnet", "Orchestrator.dll"]
