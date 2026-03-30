FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Api/UrlShortener.Api.csproj ./src/Api/
RUN dotnet restore ./src/Api/UrlShortener.Api.csproj
COPY src/ ./src/
RUN dotnet publish ./src/Api/UrlShortener.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
COPY --from=build /app/publish .
RUN chown -R appuser:appgroup /app
USER appuser
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3     CMD wget -q -O /dev/null http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "UrlShortener.Api.dll"]
