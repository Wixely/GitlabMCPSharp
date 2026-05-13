# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY GitlabMCPSharp.csproj ./
RUN dotnet restore GitlabMCPSharp.csproj

COPY . .
RUN dotnet publish GitlabMCPSharp.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    GITLABMCP_Server__Host=0.0.0.0 \
    GITLABMCP_Server__Port=5702 \
    GITLABMCP_Server__Path=/mcp \
    GITLABMCP_Gitlab__ReadOnly=true

RUN mkdir -p /app/logs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5702
VOLUME ["/app/logs"]

ENTRYPOINT ["dotnet", "GitlabMCPSharp.dll"]
