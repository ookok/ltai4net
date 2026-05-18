namespace LTAI.Desktop.Pages;

public partial class FilesPage : ContentPage
{
    private readonly string _root;

    public FilesPage()
    {
        InitializeComponent();
        _root = FileSystem.AppDataDirectory;
        _root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        UpdateRoot(Environment.CurrentDirectory.Length > 0 ? Environment.CurrentDirectory : _root);
    }

    private void UpdateRoot(string path)
    {
        TreeStack.Children.Clear();
        var dir = new DirectoryInfo(path);
        AddDirectory(dir, 0);
    }

    private void AddDirectory(DirectoryInfo dir, int depth)
    {
        var tap = new TapGestureRecognizer();
        var label = new Label
        {
            Text = $"{new string(' ', depth * 2)}📁 {dir.Name}",
            FontSize = 12,
            TextColor = Color.FromArgb("#58a6ff"),
            Margin = new Thickness(0, 2)
        };
        tap.Tapped += (_, _) =>
        {
            TreeStack.Children.Clear();
            AddDirectory(dir, 0);
        };
        label.GestureRecognizers.Add(tap);
        TreeStack.Children.Add(label);

        foreach (var sd in dir.GetDirectories().Take(10))
            if (!sd.Name.StartsWith('.') && sd.Name != "bin" && sd.Name != "obj")
                AddDirectory(sd, depth + 1);

        foreach (var f in dir.GetFiles().Take(20).OrderBy(x => x.Extension).ThenBy(x => x.Name))
        {
            var fileTap = new TapGestureRecognizer();
            var fileLabel = new Label
            {
                Text = $"{new string(' ', (depth + 1) * 2)}📄 {f.Name}",
                FontSize = 12,
                TextColor = Color.FromArgb("#c9d1d9"),
                Margin = new Thickness(0, 1)
            };
            fileTap.Tapped += (_, _) => LoadFile(f.FullName);
            fileLabel.GestureRecognizers.Add(fileTap);
            TreeStack.Children.Add(fileLabel);
        }
    }

    private async void LoadFile(string path)
    {
        FileTitle.Text = System.IO.Path.GetFileName(path);
        try
        {
            var content = await File.ReadAllTextAsync(path);
            var info = new FileInfo(path);
            FileInfo.Text = $"{info.Length / 1024}KB | {info.LastWriteTime:g}";
            FileContent.Text = content.Length > 10000 ? content[..10000] + "..." : content;
        }
        catch (Exception ex)
        {
            FileContent.Text = $"Error: {ex.Message}";
        }
    }
}
