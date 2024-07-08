using System.Diagnostics;
using ImageMagick;
using CommandLine;


public class Options
{

    [Option("tool", Required = false, Default = "platesolve", HelpText = "Tool to use, either 'platesolve' or 'astap'")]
    public string Tool { get; set; }

    [Option('r', "right-ascension", Required = true, HelpText = "Right Ascension of your target object in HH:MM:SS Format. Sample: 19h 03m 07s => 19:03:07 ")]
    public string TargetRightAscensionTime { get; set; }


    [Option('d', "declination", Required = true, HelpText = "Declination of your target object in DEG:MM:SS Format. Sample 29° 50' 28\" => 29:50:28 ")]
    public string TargetDeclination { get; set; }

    [Option('m', "monitor", Required = false, HelpText = "Directory which you want to monitor for new star images")]
    public string Directory { get; set; }

    [Option('n', "regions", Required = false, Default = 200, HelpText = "Number of regions PlateSolver2 should check")]
    public int Regions { get; set; }

    [Option('w', "width", Required = true, HelpText = "Cameras Sensors width in mm")]
    public double SensorWidthMM { get; set; }

    [Option('h', "height", Required = true, HelpText = "Cameras Sensors height in mm")]
    public double SensorHeightMM { get; set; }

    [Option('f', "focal-length", Required = true, HelpText = "Telescopes focal length, including Reducer/Flattener factor and crop-sensor")]
    public double FocalLength { get; set; }

    [Option('p', "executable-path", Required = false, HelpText = "Path of PlateSolve2.exe file")]
    public string ToolPath { get; set; }

    [Option('t', "file-write-time", Required = false, Default = 5000, HelpText = "Time to wait before processing after image file has been created")]
    public int CreateFileTimeout { get; set; }

    [Option('i', "input-file", HelpText = "Does not monitor directory, instead solves given file")]
    public string InputFile { get; set; }
}

public class SolveResult
{
    public TimeSpan CenterRA { get; set; }
    public double CenterDeclinationDegrees { get; set; }
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
        if (String.IsNullOrEmpty(ops.ToolPath))
        {
            if (ops.Tool == "astap") ops.ToolPath = "C:\\Program Files\\astap\\astap.exe";
            else ops.ToolPath = "C:\\Program Files\\PlateSolve2.28\\PlateSolve2.exe";
        }


        if (!String.IsNullOrEmpty(ops.InputFile))
        {
            OnFileReady(ops.InputFile);
            return;
        }

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
        Thread.Sleep(options.CreateFileTimeout);
        OnFileReady(e.FullPath);
    }

    private static void OnFileReady(string filename)
    {

        if (options.Tool == "astap")
        {
            // astap handles cr2 at its own...
            AnalyzeFile(filename);
            return;
        }

        if (!String.Equals(Path.GetExtension(filename), ".jpg", StringComparison.CurrentCultureIgnoreCase))
        {
            try
            {

                Console.WriteLine($"Waiting {options.CreateFileTimeout}ms before processing file to avoid file system errors.");
                var newFile = Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename).Replace(" ", "_")) + ".jpg";

                using (MagickImage image = new MagickImage(filename))
                {
                    image.Write(newFile);
                }

                if (!String.IsNullOrEmpty(options.InputFile))
                {
                    AnalyzeFile(newFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }
        else
        {
            AnalyzeFile(filename);
        }
    }

    private static void AnalyzeFile(string filename)
    {
        try
        {

            SolveResult result = null;

            if (options.Tool == "platesolve")
            {
                result = AnalyseFileWithPlateSolver(filename);
            }
            else if (options.Tool == "astap")
            {
                result = AnalyzeFileWithAstap(filename);
            }

            Console.WriteLine("Center Coordinate: ");
            Console.WriteLine($"    RA: {result.CenterRA.ToString()}");
            Console.WriteLine($"    Dec: {result.CenterDeclinationDegrees}° \"");

            var targetRaHours = TimeToHours(options.TargetRightAscensionTime);
            var trgetDecDegrees = TimeToHours(options.TargetDeclination);

            var raDelta = targetRaHours - result.CenterRA.TotalHours;
            var raDeltaCompensation = Math.Round((raDelta * 60 * 60) / 12.0, 0);

            // 1 turn to the right makes ~3°
            var declinationDelta = trgetDecDegrees - result.CenterDeclinationDegrees;
            var declinationCompensation = Math.Round(declinationDelta / 3.0, 2);

            Console.WriteLine();
            Console.WriteLine("Compensation (SkyWatcher Adventurer):");
            Console.WriteLine($"    RA: {raDelta.ToString()} => Press {(raDeltaCompensation > 0 ? "left" : "right")} button {Math.Abs(raDeltaCompensation)} seconds");
            Console.WriteLine($"    Dec: {declinationDelta.ToString()} => turn declination knob {Math.Abs(declinationCompensation)} {(declinationCompensation > 0 ? "counter-clockwise" : "clockwise")}");

        }
        catch (Exception e)
        {
            Console.WriteLine("Did not work as expected: " + e.ToString());
        }

    }

    private static SolveResult AnalyzeFileWithAstap(string filename)
    {
        // astap -f {filename} -r 20 -fov 2.5 -ra 21.0 -sdp [dec + 50]
        var targetRaHours = TimeToHours(options.TargetRightAscensionTime);
        var targetDeclinationDegrees = TimeToHours(options.TargetDeclination) + 90;

        var fovX = ((57.3 / options.FocalLength) * options.SensorWidthMM);

        var paramString = $"-r 20 -ra {targetRaHours} -sdp {targetDeclinationDegrees} -fov {fovX} -f {filename}";

        Console.WriteLine($"Calling astap {paramString}");

        var process = Process.Start(new ProcessStartInfo(options.ToolPath, paramString));
        process.WaitForExit();

        var resultFile = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename)) + ".ini");
        var resultRA = resultFile.FirstOrDefault(t => t.StartsWith("CRVAL1")).Split("=").Last().Trim();
        var resultDec = resultFile.FirstOrDefault(t => t.StartsWith("CRVAL2")).Split("=").Last().Trim();

        Console.WriteLine($"RA: {resultRA}, DEC: {resultDec}");

        var ra = double.Parse(resultRA);
        var dec = double.Parse(resultDec);

        return new SolveResult
        {
            CenterRA = TimeSpan.FromHours(ra / 360) * 24,
            CenterDeclinationDegrees = dec
        };

    }

    private static SolveResult AnalyseFileWithPlateSolver(string filename)
    {

        var targetRaRadians = ConvertToRadians(options.TargetRightAscensionTime);
        var targetDeclinationRadians = ConvertDegreeToRadians(options.TargetDeclination);

        var fovX = ((57.3 / options.FocalLength) * options.SensorWidthMM) * (Math.PI / 180);
        var fovY = ((57.3 / options.FocalLength) * options.SensorHeightMM) * (Math.PI / 180);

        var paramString = $"{targetRaRadians},{targetDeclinationRadians},{fovX},{fovY},{options.Regions},{filename},1";

        Console.WriteLine($"Calling PlateSolve2.exe {paramString}");

        var process = Process.Start(new ProcessStartInfo(options.ToolPath, paramString));
        process.WaitForExit();

        var apmFile = Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename)) + ".apm";

        var apmContent = File.ReadAllLines(apmFile);
        var firstLine = apmContent.FirstOrDefault()?.Split(",");

        var centerRaRadians = double.Parse(firstLine[0]);
        var centerDeclinationRadians = double.Parse(firstLine[1]);

        TimeSpan centerRaTimeSpan = ConvertRadiansToTimespan(centerRaRadians);

        var declinationDegrees = ConvertRadiansToDegree(centerDeclinationRadians);

        return new SolveResult
        {
            CenterRA = centerRaTimeSpan,
            CenterDeclinationDegrees = declinationDegrees
        };
    }

    private static double TimeToHours(string time)
    {
        var tokens = time.Split(":");
        return double.Parse(tokens[0]) + double.Parse(tokens[1]) / 60 + double.Parse(tokens[2]) / 3600;
    }

    private static double ConvertRadiansToDegree(double centerDeclinationRadians)
    {
        return centerDeclinationRadians / Math.PI * 180;
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