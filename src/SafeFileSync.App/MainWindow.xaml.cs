using System.Windows;
using SafeFileSync.Core;
namespace SafeFileSync.App;
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
    private void ValidatePaths(object sender, RoutedEventArgs e)
    {
        try {
            PathSafetyService.ValidatePair(SourcePath.Text, DestinationPath.Text);
            Result.Text = "경로 형식 검사 통과. 실제 경로 별칭·권한·연결·링크 검사는 아직 수행되지 않았습니다.";
        } catch (ArgumentException ex) { Result.Text = ex.Message; }
    }
}
