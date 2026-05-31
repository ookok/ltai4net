# ── LTAI Web — Multi-stage Docker Build ──
# Build: docker build -t ltai-web -f Dockerfile .
# Run:   docker run -p 5100:5100 -e DEEPSEEK_API_KEY=sk-xxx ltai-web

# ── Stage 1: Restore ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=publish /app .

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/* && \
    mkdir -p .livingtree/sessions

# Create non-root user for security
RUN adduser --disabled-password --gecos '' ltai && chown -R ltai:ltai /app
USER ltai

EXPOSE 5100

ENV ASPNETCORE_URLS=http://+:5100
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "LTAI.Web.dll"]
