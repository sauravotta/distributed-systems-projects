FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG PROJECT
COPY . .
RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" --no-restore --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet"]
