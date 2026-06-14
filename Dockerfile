# ── LTAI Web — Multi-stage Docker Build (amd64 + arm64) ──
# Build: docker buildx build --platform linux/amd64,linux/arm64 -t ltai-web -f Dockerfile .
# Run:   docker run -p 5100:5100 -e DEEPSEEK_API_KEY=sk-xxx ltai-web

ARG TARGETPLATFORM

# ── Stage 1: Restore ──
# For reproducible production builds, replace the :10.0 tag with a digest pin:
# FROM .../dotnet/sdk:10.0@sha256:abc123...
FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY Directory.Build.props .
COPY src/LTAI.Core/LTAI.Core.csproj src/LTAI.Core/
COPY src/LTAI.AI/LTAI.AI.csproj src/LTAI.AI/
COPY src/LTAI.Agent/LTAI.Agent.csproj src/LTAI.Agent/
COPY src/LTAI.Web/LTAI.Web.csproj src/LTAI.Web/
RUN dotnet restore src/LTAI.Web/LTAI.Web.csproj

# ── Stage 2: Build ──
FROM restore AS build
COPY . .
WORKDIR /src/src/LTAI.Web
RUN dotnet build -c Release --no-restore

# ── Stage 3: Publish ──
FROM build AS publish
RUN dotnet publish -c Release -o /app --no-build

# ── Stage 4: Runtime ──
FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app .

# Install curl for health checks
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* && \
    mkdir -p .livingtree/sessions

# Create non-root user for security
RUN adduser --disabled-password --gecos '' ltai && chown -R ltai:ltai /app
USER ltai

EXPOSE 5100

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
  CMD curl -sf http://localhost:5100/health || exit 1

ENV ASPNETCORE_URLS=http://+:5100
ENV ASPNETCORE_ENVIRONMENT=Production
ENV LTAI_DATA_DIR=/app/.livingtree

ENTRYPOINT ["dotnet", "LTAI.Web.dll"]
