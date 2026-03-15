cat src/VvCash/Views/PosView.axaml | sed 's/Text="{Binding SelectedCategory.Name, StringFormat='\''{}Catalog - {0}'\'', TargetNullValue='\''All Categories'\''}"/Text="{Binding SelectedCategoryName, StringFormat='\''{}Catalog - {0}'\''}"/g' > temp.axaml
mv temp.axaml src/VvCash/Views/PosView.axaml
