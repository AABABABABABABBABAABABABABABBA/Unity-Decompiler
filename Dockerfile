# Build and run the web server (UnityDecompiler.Server) on a VPS / dedicated server.
#   docker build -t unity-decompiler .
#   docker run -p 8080:8080 -v ud-data:/data unity-decompiler
# Then open http://your-server:8080
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/UnityDecompiler.Core/ src/UnityDecompiler.Core/
COPY src/UnityDecompiler.Server/ src/UnityDecompiler.Server/
RUN dotnet publish src/UnityDecompiler.Server/UnityDecompiler.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Config via environment variables (all optional):
ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIR=/data \
    MAX_UPLOAD_MB=2048 \
    RETENTION_HOURS=6
#   ACCESS_PASSWORD=...   set this to require a password
#   COPY_STREAMING_ASSETS=true   include StreamingAssets in results (uses more disk)
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "UnityDecompiler.Server.dll"]
