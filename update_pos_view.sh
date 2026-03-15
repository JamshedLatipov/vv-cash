# The error is: [Binding]An error occurred binding 'Text' to 'SelectedCategory.Name' at 'SelectedCategory': 'Value is null.' (TextBlock #35455590)
# This is because I used TargetNullValue='All Categories' but the Avalonia XAML engine throws a warning when SelectedCategory is null and you try to bind to SelectedCategory.Name, even if TargetNullValue is provided.
# A better way is to bind to SelectedCategory directly and use a ValueConverter, or fallback.
