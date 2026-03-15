sed -i 's/return FetchPaginatedAsync("products\/category");/return FetchPaginatedAsync("cashes\/category");/g' src/VvCash/Services/Api/CategoryService.cs
sed -i 's/return FetchPaginatedAsync("cashes\/category\/show-on-cash");/return FetchPaginatedAsync("cashes\/category\/show-on-cash");/g' src/VvCash/Services/Api/CategoryService.cs
