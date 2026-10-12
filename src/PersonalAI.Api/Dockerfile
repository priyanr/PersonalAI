FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PersonalAI.slnx .
COPY src/PersonalAI.Sandbox.Protocol/PersonalAI.Sandbox.Protocol.csproj src/PersonalAI.Sandbox.Protocol/
COPY src/PersonalAI.Core/PersonalAI.Core.csproj src/PersonalAI.Core/
COPY src/PersonalAI.Api/PersonalAI.Api.csproj src/PersonalAI.Api/
RUN dotnet restore src/PersonalAI.Api/PersonalAI.Api.csproj

COPY src/PersonalAI.Sandbox.Protocol/ src/PersonalAI.Sandbox.Protocol/
COPY src/PersonalAI.Core/ src/PersonalAI.Core/
COPY src/PersonalAI.Api/ src/PersonalAI.Api/
RUN dotnet publish src/PersonalAI.Api/PersonalAI.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "PersonalAI.Api.dll"]
