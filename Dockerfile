#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/core/runtime:5.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/core/sdk:5.0-alpine AS build
WORKDIR /src
COPY ["AstralProjection.csproj", ""]
RUN dotnet restore "./AstralProjection.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "AstralProjection.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AstralProjection.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app 
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AstralProjection.dll"]