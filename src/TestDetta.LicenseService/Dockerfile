# The license service as a container: build with the SDK, run on the ASP.NET runtime as a non-root user.
# Images are pinned by digest; update them deliberately (docker buildx imagetools inspect <image>:10.0).
# Build from the repository root: docker build -f src/TestDetta.LicenseService/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
# Restore exactly what the committed lock files say (Directory.Build.props turns locked mode on for CI).
ENV CI=true
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/TestDetta.Core/ src/TestDetta.Core/
COPY src/TestDetta.LicenseService/ src/TestDetta.LicenseService/
RUN dotnet publish src/TestDetta.LicenseService/TestDetta.LicenseService.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --from=build /app ./
# The image's built-in unprivileged user; the signing key is mounted read-only (Licenses__KeyFile),
# never baked into the image or passed as an environment variable.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TestDetta.LicenseService.dll"]
