FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/DockFlow.Web/DockFlow.Web.csproj -c Release -o /out/web -p:UseAppHost=false -p:RestoreLockedMode=true \
    && dotnet publish src/DockFlow.Worker/DockFlow.Worker.csproj -c Release -o /out/worker -p:UseAppHost=false -p:RestoreLockedMode=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21 AS web
WORKDIR /app
COPY --from=build /out/web/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "DockFlow.Web.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21 AS worker
WORKDIR /app
COPY --from=build /out/worker/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "DockFlow.Worker.dll"]
