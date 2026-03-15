import re

with open('src/VvCash/Views/PosView.axaml', 'r') as f:
    content = f.read()

# Replace sidebar buttons
sidebar_pattern = r'<Button Classes="CategoryButton" Classes.Active="\{Binding SelectedCategory, Converter=\{StaticResource StringEqualityToBoolConverter\}, ConverterParameter=\'All\'\}" Command="\{Binding SelectCategoryCommand\}" CommandParameter="All">.*?<Button Classes="CategoryButton" Classes.Active="\{Binding SelectedCategory, Converter=\{StaticResource StringEqualityToBoolConverter\}, ConverterParameter=\'Sale\'\}" Command="\{Binding SelectCategoryCommand\}" CommandParameter="Sale" Foreground="\{StaticResource Red500Brush\}">.*?</Button>'
sidebar_replacement = """<Button Classes="CategoryButton" Classes.Active="{Binding IsViewingCategories}" Command="{Binding SelectCategoryCommand}">
                        <StackPanel HorizontalAlignment="Center" Spacing="4">
                            <material:MaterialIcon Kind="ViewGrid" Width="24" Height="24" />
                            <TextBlock Text="ALL" FontSize="10" FontWeight="Bold" HorizontalAlignment="Center" LetterSpacing="1"/>
                        </StackPanel>
                    </Button>
                    <ItemsControl ItemsSource="{Binding QuickCategories}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate DataType="models:Category">
                                <Button Classes="CategoryButton"
                                        Command="{Binding DataContext.SelectCategoryCommand, ElementName=RootWindow}"
                                        CommandParameter="{Binding}">
                                    <StackPanel HorizontalAlignment="Center" Spacing="4">
                                        <material:MaterialIcon Kind="FolderOutline" Width="24" Height="24" />
                                        <TextBlock Text="{Binding Name}" FontSize="10" FontWeight="Bold" HorizontalAlignment="Center" LetterSpacing="1" TextWrapping="Wrap" TextAlignment="Center"/>
                                    </StackPanel>
                                </Button>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>"""

content = re.sub(sidebar_pattern, sidebar_replacement, content, flags=re.DOTALL)

# Replace Catalog Header
header_pattern = r'<TextBlock Grid.Column="0" Text="\{Binding SelectedCategory, StringFormat=\'\{\}Catalog - \{0\}\'\}" FontSize="24" FontWeight="Bold" Foreground="\{StaticResource Slate900Brush\}" VerticalAlignment="Center"/>'
header_replacement = """<TextBlock Grid.Column="0" Text="{Binding SelectedCategory.Name, StringFormat='{}Catalog - {0}', TargetNullValue='All Categories'}" FontSize="24" FontWeight="Bold" Foreground="{StaticResource Slate900Brush}" VerticalAlignment="Center"/>"""

content = re.sub(header_pattern, header_replacement, content)

# Replace ScrollViewer content
scroll_pattern = r'<ScrollViewer Grid.Row="1">\s*<ItemsControl ItemsSource="\{Binding Products\}">'
scroll_replacement = """<ScrollViewer Grid.Row="1">
                                <StackPanel Spacing="0">
                                <ItemsControl ItemsSource="{Binding AllCategories}" IsVisible="{Binding IsViewingCategories}">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate>
                                            <WrapPanel/>
                                        </ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate DataType="models:Category">
                                            <Border Width="220" Height="100" Margin="0,0,16,16" Cursor="Hand" BorderBrush="{StaticResource Slate200Brush}" BorderThickness="1" Background="White" CornerRadius="8">
                                                <Button Classes="Transparent"
                                                        Command="{Binding DataContext.SelectCategoryCommand, ElementName=RootWindow}"
                                                        CommandParameter="{Binding}"
                                                        Padding="0" HorizontalAlignment="Stretch" VerticalAlignment="Stretch">
                                                    <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Spacing="8">
                                                        <material:MaterialIcon Kind="FolderOutline" Width="32" Height="32" Foreground="{StaticResource Slate500Brush}"/>
                                                        <TextBlock Text="{Binding Name}" FontWeight="Bold" FontSize="16" Foreground="{StaticResource Slate900Brush}" TextAlignment="Center" TextWrapping="Wrap"/>
                                                    </StackPanel>
                                                </Button>
                                            </Border>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>

                                <ItemsControl ItemsSource="{Binding Products}" IsVisible="{Binding !IsViewingCategories}">"""

content = re.sub(scroll_pattern, scroll_replacement, content)

# Replace ScrollViewer closing
scroll_close_pattern = r'(</ItemsControl>)\s*(</ScrollViewer>)'
scroll_close_replacement = r'\1\n                                </StackPanel>\n                            \2'
content = re.sub(scroll_close_pattern, scroll_close_replacement, content, count=1)

with open('src/VvCash/Views/PosView.axaml', 'w') as f:
    f.write(content)
