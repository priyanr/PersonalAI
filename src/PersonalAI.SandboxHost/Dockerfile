# No "docker" CLI is needed in the runtime image — DockerSandboxManager talks to the Engine API
# directly over the mounted socket via Docker.DotNet.Enhanced, so the runtime stage below only needs
# the plain .NET runtime, not the SDK or aspnet's ASP.NET Core bits (this process has no web server).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PersonalAI.slnx .
COPY src/PersonalAI.Sandbox.Protocol/PersonalAI.Sandbox.Protocol.csproj src/PersonalAI.Sandbox.Protocol/
COPY src/PersonalAI.SandboxHost/PersonalAI.SandboxHost.csproj src/PersonalAI.SandboxHost/
RUN dotnet restore src/PersonalAI.SandboxHost/PersonalAI.SandboxHost.csproj

COPY src/PersonalAI.Sandbox.Protocol/ src/PersonalAI.Sandbox.Protocol/
COPY src/PersonalAI.SandboxHost/ src/PersonalAI.SandboxHost/
RUN dotnet publish src/PersonalAI.SandboxHost/PersonalAI.SandboxHost.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Broker:HostKey, Broker:ApiUrl etc are supplied as Broker__* environment variables at `docker run`/
# compose time (see docker-compose.yml's sandboxhost service) rather than baked into the image.
ENTRYPOINT ["dotnet", "PersonalAI.SandboxHost.dll"]
