FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/TiendaMiriam.Domain/TiendaMiriam.Domain.csproj src/TiendaMiriam.Domain/
COPY src/TiendaMiriam.Infrastructure/TiendaMiriam.Infrastructure.csproj src/TiendaMiriam.Infrastructure/
COPY src/TiendaMiriam.Api/TiendaMiriam.Api.csproj src/TiendaMiriam.Api/
RUN dotnet restore src/TiendaMiriam.Api/TiendaMiriam.Api.csproj
COPY src/TiendaMiriam.Domain/ src/TiendaMiriam.Domain/
COPY src/TiendaMiriam.Infrastructure/ src/TiendaMiriam.Infrastructure/
COPY src/TiendaMiriam.Api/ src/TiendaMiriam.Api/
RUN dotnet publish src/TiendaMiriam.Api/TiendaMiriam.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TiendaMiriam.Api.dll"]
