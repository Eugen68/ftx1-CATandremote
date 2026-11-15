using Ftx1.Audio;
internal static class Program
{
    [STAThread] static void Main(string[] args) => AudioWorkerHost.Run(() => new SimVoiceDevice(), args.Contains("--hang-on-stop"));
}
