# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file first for restore-layer caching.
COPY src/JPVOS/JPVOS.csproj src/JPVOS/
RUN dotnet restore src/JPVOS/JPVOS.csproj

# Copy application source and the canonical governance artifacts required at publish/runtime.
COPY src/JPVOS/ src/JPVOS/
COPY .jpv/governance/systemic-access-hygiene.json .jpv/governance/systemic-access-hygiene.json
COPY .jpv/governance/privileged-action-governance.json .jpv/governance/privileged-action-governance.json

# Build and publish (--no-restore since restore already ran).
RUN dotnet publish src/JPVOS/JPVOS.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Use non-root user for security.
USER 1654

# Copy published app.
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "JPVOS.dll"]
