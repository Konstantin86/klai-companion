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

COPY --from=build /app/publish .

# Create the data directory for SQLite so volume mapping doesn't fail
RUN mkdir -p /app/data

ENTRYPOINT ["dotnet", "klai.dll"]