FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY FireflyCategorizer.DotNet.csproj ./
RUN dotnet restore FireflyCategorizer.DotNet.csproj

COPY . ./
RUN dotnet publish FireflyCategorizer.DotNet.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "FireflyCategorizer.DotNet.dll"]