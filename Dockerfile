FROM mcr.microsoft.com/dotnet/runtime:10.0-preview AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY ["src/LTAI.Host/LTAI.Host.csproj", "src/LTAI.Host/"]
COPY ["src/LTAI.Core/LTAI.Core.csproj", "src/LTAI.Core/"]
COPY ["src/LTAI.AI/LTAI.AI.csproj", "src/LTAI.AI/"]
COPY ["src/LTAI.Vector/LTAI.Vector.csproj", "src/LTAI.Vector/"]
COPY ["src/LTAI.Web/LTAI.Web.csproj", "src/LTAI.Web/"]
COPY ["src/LTAI.MAF/LTAI.MAF.csproj", "src/LTAI.MAF/"]
COPY ["src/LTAI.DNA/LTAI.DNA.csproj", "src/LTAI.DNA/"]
COPY ["src/LTAI.Capability/LTAI.Capability.csproj", "src/LTAI.Capability/"]
COPY ["src/LTAI.Browser/LTAI.Browser.csproj", "src/LTAI.Browser/"]
COPY ["src/LTAI.Document/LTAI.Document.csproj", "src/LTAI.Document/"]
COPY ["src/LTAI.Network/LTAI.Network.csproj", "src/LTAI.Network/"]
COPY ["src/LTAI.TreeLLM/LTAI.TreeLLM.csproj", "src/LTAI.TreeLLM/"]
COPY ["src/LTAI.Execution/LTAI.Execution.csproj", "src/LTAI.Execution/"]
COPY ["src/LTAI.Economy/LTAI.Economy.csproj", "src/LTAI.Economy/"]
COPY ["src/LTAI.Sandbox/LTAI.Sandbox.csproj", "src/LTAI.Sandbox/"]
COPY ["src/LTAI.Metrics/LTAI.Metrics.csproj", "src/LTAI.Metrics/"]
COPY ["src/LTAI.Memory/LTAI.Memory.csproj", "src/LTAI.Memory/"]
COPY ["src/LTAI.Multimodal/LTAI.Multimodal.csproj", "src/LTAI.Multimodal/"]
COPY ["src/LTAI.Market/LTAI.Market.csproj", "src/LTAI.Market/"]
COPY ["src/LTAI.Cell/LTAI.Cell.csproj", "src/LTAI.Cell/"]
COPY ["src/LTAI.Templates/LTAI.Templates.csproj", "src/LTAI.Templates/"]
RUN dotnet restore "src/LTAI.Host/LTAI.Host.csproj"
COPY . .
WORKDIR "/src/src/LTAI.Host"
RUN dotnet build "LTAI.Host.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "LTAI.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY ["config/", "/app/config/"]
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "LTAI.Host.dll"]
