FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY GitlabMCPSharp.csproj ./
RUN dotnet restore GitlabMCPSharp.csproj

COPY . ./
RUN dotnet publish GitlabMCPSharp.csproj \
    -c Release \
    --no-restore \
    --self-contained false \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S gitlabmcp && adduser -S gitlabmcp -G gitlabmcp

COPY --from=build /app/publish ./
RUN mkdir -p /app/logs && chown -R gitlabmcp:gitlabmcp /app

USER gitlabmcp
EXPOSE 5100

ENTRYPOINT ["dotnet", "GitlabMCPSharp.dll"]
