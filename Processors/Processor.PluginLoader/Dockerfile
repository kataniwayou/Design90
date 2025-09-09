# Use the official .NET 9.0 runtime as the base image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

# Install FFmpeg for AudioConverterPlugin support
RUN apt-get update && \
    apt-get install -y ffmpeg && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Use the official .NET 9.0 SDK for building
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution file
COPY FlowOrchestrator.sln .

# Copy all project files for dependency resolution
COPY Processors/Processor.PluginLoader/Processor.PluginLoader.csproj Processors/Processor.PluginLoader/
COPY Processors/Processor.Base/Processor.Base.csproj Processors/Processor.Base/
COPY Shared/Shared.Configuration/Shared.Configuration.csproj Shared/Shared.Configuration/
COPY Shared/Shared.Correlation/Shared.Correlation.csproj Shared/Shared.Correlation/
COPY Shared/Shared.Entities/Shared.Entities.csproj Shared/Shared.Entities/
COPY Shared/Shared.Exceptions/Shared.Exceptions.csproj Shared/Shared.Exceptions/
COPY Shared/Shared.Extensions/Shared.Extensions.csproj Shared/Shared.Extensions/
COPY Shared/Shared.HealthChecks/Shared.HealthChecks.csproj Shared/Shared.HealthChecks/
COPY Shared/Shared.MassTransit/Shared.MassTransit.csproj Shared/Shared.MassTransit/
COPY Shared/Shared.Models/Shared.Models.csproj Shared/Shared.Models/
COPY Shared/Shared.MongoDB/Shared.MongoDB.csproj Shared/Shared.MongoDB/
COPY Shared/Shared.Repositories/Shared.Repositories.csproj Shared/Shared.Repositories/
COPY Shared/Shared.Services/Shared.Services.csproj Shared/Shared.Services/

# Restore dependencies
RUN dotnet restore Processors/Processor.PluginLoader/Processor.PluginLoader.csproj

# Copy all source code
COPY . .

# Build the application with real hash generation
WORKDIR /src/Processors/Processor.PluginLoader
RUN dotnet build Processor.PluginLoader.csproj -c Release -o /app/build

# Publish the application with real hash generation
FROM build AS publish
RUN dotnet publish Processor.PluginLoader.csproj -c Release -o /app/publish /p:UseAppHost=false

# Final stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create a non-root user
RUN groupadd -r appuser && useradd -r -g appuser appuser
RUN chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "Processor.PluginLoader.dll"]
