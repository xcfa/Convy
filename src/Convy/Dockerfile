# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration).
# It builds for the host architecture only, so its RUN executes natively.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# Build on the native build platform and cross-publish for $TARGETARCH. This keeps the
# heavy work (restore, ANTLR codegen, compile) native, so multi-arch images build
# without QEMU emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy csproj files first (preserving the src/ layout) so restore is cached.
COPY ["src/Convy/Convy.csproj", "src/Convy/"]
COPY ["src/Convy.Services/Convy.Services.csproj", "src/Convy.Services/"]
COPY ["src/Convy.Data/Convy.Data.csproj", "src/Convy.Data/"]
COPY ["src/Convy.PathExpressions/Convy.PathExpressions.csproj", "src/Convy.PathExpressions/"]
COPY ["src/Convy.Infrastructure/Convy.Infrastructure.csproj", "src/Convy.Infrastructure/"]
RUN dotnet restore "src/Convy/Convy.csproj"

COPY . .
# Map Docker's TARGETARCH (amd64/arm64) to the .NET RID arch (x64/arm64). The -a flag
# pulls the right native bits (e.g. SQLite's e_sqlite3) for the target.
RUN DOTNET_ARCH="$(echo "$TARGETARCH" | sed 's/amd64/x64/')" \
 && dotnet publish "src/Convy/Convy.csproj" -c "$BUILD_CONFIGURATION" -a "$DOTNET_ARCH" -o /app/publish /p:UseAppHost=false

# Stage an (app-owned) data directory to copy into the final image without a RUN, so the
# final, target-arch stage executes no foreign code.
RUN mkdir -p /app/state && touch /app/state/.keep

# Final, target-architecture image. Contains only COPY/metadata — no RUN — so it needs
# no emulation when built for a foreign architecture.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Convy.dll"]
