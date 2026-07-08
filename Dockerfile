FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/RagNet.Mcp/RagNet.Mcp.csproj src/RagNet.Mcp/
COPY src/RagNet.Core/RagNet.Core.csproj src/RagNet.Core/
COPY src/RagNet.Analysis/RagNet.Analysis.csproj src/RagNet.Analysis/
COPY src/RagNet.Composition/RagNet.Composition.csproj src/RagNet.Composition/
COPY src/RagNet.Infrastructure/RagNet.Infrastructure.csproj src/RagNet.Infrastructure/
RUN dotnet restore src/RagNet.Mcp/RagNet.Mcp.csproj

COPY . .
RUN dotnet publish src/RagNet.Mcp/RagNet.Mcp.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:7331
EXPOSE 7331

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ragnet-mcp.dll"]
