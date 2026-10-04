# The license service as a container: build with the SDK, run on the ASP.NET runtime as a non-root user.
# Images are pinned by digest; update them deliberately (docker buildx imagetools inspect <image>:10.0).
# Build from the repository root: docker build -f src/TestDetta.LicenseService/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:5eda31303f8b1b852e79d89fee9f4274cc9ec0f3c96e19e1b837e49d8df288e0 AS build
WORKDIR /source
# Restore exactly what the committed lock files say (Directory.Build.props turns locked mode on for CI).
ENV CI=true
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/TestDetta.Core/ src/TestDetta.Core/
COPY src/TestDetta.LicenseService/ src/TestDetta.LicenseService/
RUN dotnet publish src/TestDetta.LicenseService/TestDetta.LicenseService.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a47e0d1ec583b8f18c72bff4191257b538958f15a830a4175c65adaaff1ae47c
WORKDIR /app
COPY --from=build /app ./
# The image's built-in unprivileged user; the signing key is mounted read-only (Licenses__KeyFile),
# never baked into the image or passed as an environment variable.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TestDetta.LicenseService.dll"]
