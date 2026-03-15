cat src/VvCash/ViewModels/PosViewModel.cs | sed 's/\[ObservableProperty\] private Category? _selectedCategory;/\[ObservableProperty\] private Category? _selectedCategory;\n    public string SelectedCategoryName => SelectedCategory?.Name ?? "All Categories";/g' > temp.cs
cat temp.cs | sed -z 's/SelectedCategory = category;\n        SearchQuery = string.Empty;/SelectedCategory = category;\n        OnPropertyChanged(nameof(SelectedCategoryName));\n        SearchQuery = string.Empty;/g' > src/VvCash/ViewModels/PosViewModel.cs
rm temp.cs
