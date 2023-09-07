using System.Diagnostics;
using ImageMagick;
using CommandLine;


public class Options
{
    [Option('r', "right-ascension", Required = true, HelpText = "Right Ascension of your target object in HH:MM:SS Format. Sample: 19h 03m 07s => 19:03:07 ")]
    public string TargetRightAscensionTime { get; set; }


    [Option('d', "declination", Required = true, HelpText = "Declination of your target object in DEG:MM:SS Format. Sample 29° 50' 28\" => 29:50:28 ")]
    public string TargetDeclination { get; set; }

    [Option('m', "monitor", Required = true, HelpText = "Directory which you want to monitor for new star images")]
    public string Directory { get; set; }

    [Option('n', "regions", Required = false, Default = 200, HelpText = "Number of regions PlateSolver2 should check")]
    public int Regions { get; set; }

    [Option('w', "width", Required = true, HelpText = "Cameras Sensors width in mm")]
    public double SensorWidthMM { get; set; }

    [Option('h', "height", Required = true, HelpText = "Cameras Sensors height in mm")]
    public double SensorHeightMM { get; set; }

    [Option('f', "focal-length", Required = true, HelpText = "Telescopes focal length, including Reducer/Flattener factor and crop-sensor")]
    public double FocalLength { get; set; }

    [Option('p', "plate-solver2-path", Required = false, Default = "C:\\Program Files\\PlateSolve2.28\\PlateSolve2.exe", HelpText = "Path of PlateSolve2.exe file")]
    public string PlateSolver2Path { get; set; }

    [Option('t', "file-write-time", Required = false, Default = 5000, HelpText = "Time to wait before processing after image file has been created")]
    public int CreateFileTimeout { get; set; }
}


internal class Program
{


    private static Options options;

    private static void Main(string[] args)
    {

        CommandLine.Parser.Default.ParseArguments<Options>(args)
           .WithParsed(RunOptions)
           .WithNotParsed(HandleError);

    }


    private static void RunOptions(Options ops)
    {
        options = ops;

        using var filewatcher = new FileSystemWatcher(options.Directory);
        filewatcher.EnableRaisingEvents = true;
        filewatcher.Created += OnFileCreated;
        filewatcher.Filters.Add("*.cr2");
        filewatcher.Filters.Add("*.jpg");
        filewatcher.Filters.Add("*.png");

        Console.WriteLine($"Waiting for images in directory {options.Directory}");
        Console.ReadLine();
    }

    private static void HandleError(IEnumerable<Error> errors)
    {
        Console.WriteLine("Error, please check help");
    }

    private static void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!String.Equals(Path.GetExtension(e.FullPath), ".jpg", StringComparison.CurrentCultureIgnoreCase))
        {
            try
            {

                Console.WriteLine($"Waiting {options.CreateFileTimeout}ms before processing file to avoid file system errors.");
                Thread.Sleep(options.CreateFileTimeout);

                using (MagickImage image = new MagickImage(e.FullPath))
                {
                    var newFile = Path.Combine(Path.GetDirectoryName(e.FullPath), Path.GetFileNameWithoutExtension(e.FullPath).Replace(" ", "_")) + ".jpg";
                    image.Write(newFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
        else
        {
            AnalyzeFile(e.FullPath);
        }
    }

    private static void AnalyzeFile(string filename)
    {
        try
        {

            var targetRaRadians = ConvertToRadians(options.TargetRightAscensionTime);
            var targetDeclinationRadians = ConvertDegreeToRadians(options.TargetDeclination);

            var fovX = ((57.3 / options.FocalLength) * options.SensorWidthMM) * (Math.PI / 180);
            var fovY = ((57.3 / options.FocalLength) * options.SensorHeightMM) * (Math.PI / 180);

            var paramString = $"{targetRaRadians},{targetDeclinationRadians},{fovX},{fovY},{options.Regions},{filename},1";

            Console.WriteLine($"Calling PlateSolve2.exe {paramString}");

            var process = Process.Start(new ProcessStartInfo("C:\\Program Files\\PlateSolve2.28\\PlateSolve2.exe", paramString));
            process.WaitForExit();

            var apmFile = Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename)) + ".apm";

            var apmContent = File.ReadAllLines(apmFile);
            var firstLine = apmContent.FirstOrDefault()?.Split(",");

            var centerRaRadians = double.Parse(firstLine[0]);
            var centerDeclinationRadians = double.Parse(firstLine[1]);

            TimeSpan centerRaTimeSpan = ConvertRadiansToTimespan(centerRaRadians);

            var (declinationDegrees, declinationMinutes, declinationSeconds) = ConvertRadiansToDegree(centerDeclinationRadians);

            Console.WriteLine("Center Coordinate: ");
            Console.WriteLine($"    RA: {centerRaTimeSpan.ToString()}");
            Console.WriteLine($"    Dec: {declinationDegrees}° {Math.Floor(declinationMinutes)}' {Math.Floor(declinationSeconds)}\"");


            var raDelta = targetRaRadians - centerRaRadians;
            var raDeltaCompensation = Math.Round(ConvertRadiansToTimespan(raDelta).TotalSeconds / 12.0, 0);

            // 1 turn to the right makes ~3°
            var declinationDelta = targetDeclinationRadians - centerDeclinationRadians;
            var declinationCompensation = Math.Round(declinationDelta / (3.0 * Math.PI / 180), 2);

            Console.WriteLine();
            Console.WriteLine("Compensation (SkyWatcher Adventurer):");
            Console.WriteLine($"    RA: {ConvertRadiansToTimespan(raDelta)} => Press {(raDelta > 0 ? "left" : "right")} button {Math.Abs(raDeltaCompensation)} seconds");
            Console.WriteLine($"    Dec: {ConvertRadiansToDegree(declinationDelta)} => turn declination knob {Math.Abs(declinationCompensation)} {(declinationCompensation > 0 ? "counter-clockwise" : "clockwise")}");

            Console.WriteLine(apmFile);
            Console.WriteLine(paramString);
        }
        catch (Exception e)
        {
            Console.WriteLine("Did not work as expected: " + e.ToString());
        }
    }

    private static (double degree, double minutes, double seconds) ConvertRadiansToDegree(double centerDeclinationRadians)
    {
        var isNegative = centerDeclinationRadians < 0;
        centerDeclinationRadians = Math.Abs(centerDeclinationRadians);

        var declination = centerDeclinationRadians / Math.PI * 180;
        var declinationDegrees = Math.Floor(declination);
        var declinationMinutes = (declination - declinationDegrees) * 60;
        var declinationSeconds = (declinationMinutes - Math.Floor(declinationMinutes)) * 60;

        if (isNegative) declinationDegrees = 0 - declinationDegrees;

        return (declinationDegrees, Math.Floor(declinationMinutes), Math.Floor(declinationSeconds));
    }

    private static TimeSpan ConvertRadiansToTimespan(double centerRaRadians)
    {
        var centerRa = 24 / 360.0 * (centerRaRadians / Math.PI * 180);
        var centerRaTimeSpan = TimeSpan.FromHours(centerRa);
        return centerRaTimeSpan;
    }

    public static double ConvertToRadians(string raString)
    {
        // Split the RA string into hours, minutes, seconds, and fractional seconds
        string[] parts = raString.Split(':');

        if (parts.Length != 3)
        {
            throw new ArgumentException("Invalid RA string format. Use hh:mm:ss.sss");
        }

        try
        {
            double hours = double.Parse(parts[0]);
            double minutes = double.Parse(parts[1]);
            double seconds = double.Parse(parts[2]);

            // Calculate the total hours
            double totalHours = hours + minutes / 60.0 + seconds / 3600.0;

            // Convert to radians (24 hours = 2π radians)
            double radians = totalHours / 24.0 * (2 * Math.PI);

            return radians;
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid RA string format. Use numeric values.");
        }
    }



    public static double ConvertDegreeToRadians(string degreeString)
    {
        // Split the degree string into degrees, minutes, and seconds
        string[] parts = degreeString.Split(':');

        if (parts.Length != 3)
        {
            throw new ArgumentException("Invalid degree string format. Use dd° mm' ss\"");
        }

        try
        {
            int degrees = int.Parse(parts[0].Trim());
            int minutes = int.Parse(parts[1].Trim());
            double seconds = double.Parse(parts[2].Trim());

            bool isNegative = degrees < 0;

            // Calculate the total degrees
            double totalDegrees = Math.Abs(degrees) + minutes / 60.0 + seconds / 3600.0;

            // Convert to radians (180 degrees = π radians)
            double radians = totalDegrees * (Math.PI / 180.0);

            if (isNegative) radians = 0 - radians;

            return radians;
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid degree string format. Use numeric values.");
        }
    }
}