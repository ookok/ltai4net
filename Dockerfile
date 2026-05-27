FROM mcr.microsoft.com/dotnet/runtime:10.0-preview AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY ["src/LTAI.Host/LTAI.Host.csproj", "src/LTAI.Host/"]
COPY ["src/LTAI.Core/LTAI.Core.csproj", "src/LTAI.Core/"]
COPY ["src/LTAI.Models/LTAI.Models.csproj", "src/LTAI.Models/"]
COPY ["src/LTAI.AI/LTAI.AI.csproj", "src/LTAI.AI/"]
COPY ["src/LTAI.Knowledge/LTAI.Knowledge.csproj", "src/LTAI.Knowledge/"]
COPY ["src/LTAI.Tools/LTAI.Tools.csproj", "src/LTAI.Tools/"]
COPY ["src/LTAI.Planning/LTAI.Planning.csproj", "src/LTAI.Planning/"]
COPY ["src/LTAI.Agent/LTAI.Agent.csproj", "src/LTAI.Agent/"]
COPY ["src/LTAI.Infra/LTAI.Infra.csproj", "src/LTAI.Infra/"]
COPY ["src/LTAI.DNA/LTAI.DNA.csproj", "src/LTAI.DNA/"]
COPY ["src/LTAI.Economy/LTAI.Economy.csproj", "src/LTAI.Economy/"]
COPY ["src/LTAI.Web/LTAI.Web.csproj", "src/LTAI.Web/"]
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
