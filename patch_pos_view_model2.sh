cat src/VvCash/ViewModels/PosViewModel.cs | sed 's/private async Task SelectCategory(string category)/private async Task SelectCategory(Category? category)/g' > temp.cs
cat temp.cs | sed 's/SelectedCategory = category;/SelectedCategory = category;/g' > temp2.cs
cat temp2.cs | sed 's/await LoadProductsAsync(category);/if (category == null) { IsViewingCategories = true; Products.Clear(); } else { IsViewingCategories = false; await LoadProductsAsync(category.Id); }/g' > temp.cs
mv temp.cs src/VvCash/ViewModels/PosViewModel.cs
rm temp2.cs
