FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY nuget.config LeadIntel.slnx ./
COPY src/LeadIntel/LeadIntel.csproj src/LeadIntel/
RUN dotnet restore src/LeadIntel/LeadIntel.csproj
COPY src/ src/
RUN dotnet publish src/LeadIntel/LeadIntel.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /home/app/.aspnet /app/reports && chown app /home/app/.aspnet /app/reports
# the image's non-root user; sign-in keys persist in /home/app/.aspnet (a volume) so restarts don't log everyone out
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "LeadIntel.dll"]
