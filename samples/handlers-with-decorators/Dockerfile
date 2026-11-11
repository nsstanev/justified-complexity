# Build stage: restore first so package downloads are cached between code changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/HandlersWithDecorators.csproj .
RUN dotnet restore
COPY src/ .
RUN dotnet publish -c Release -o /app --no-restore

# Runtime stage: ASP.NET Core runtime only, running as the image's non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "HandlersWithDecorators.dll"]
