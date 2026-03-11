# Build and Publish
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /klai

# Copy csproj and restore as distinct layers
COPY ["klai.csproj", "./"]
RUN dotnet restore "klai.csproj"

# Copy everything else and build
COPY . .
RUN dotnet publish "klai.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Production Runtime
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

# Optional: Add a non-root user for security
RUN adduser --disabled-password \
  --home /app \
  --gecos '' appuser && chown -R appuser /app
USER appuser

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "klai.dll"]