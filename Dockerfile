FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/RoleCall.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__RoleCall="Data Source=/data/role-call.db"
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "RoleCall.Web.dll"]
