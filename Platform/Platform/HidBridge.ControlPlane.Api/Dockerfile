FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Platform/ Platform/
RUN dotnet restore Platform/Platform/HidBridge.ControlPlane.Api/HidBridge.ControlPlane.Api.csproj
RUN dotnet publish Platform/Platform/HidBridge.ControlPlane.Api/HidBridge.ControlPlane.Api.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:18093
EXPOSE 18093

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HidBridge.ControlPlane.Api.dll"]
