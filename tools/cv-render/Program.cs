// cv-render — renders a realistic sample CV to a real PDF and verifies it.
// Proves the CircleAI.Documents engine end-to-end (font resolver + MigraDoc
// render + valid PDF bytes) on the desktop before the Huawei.

using CircleAI.Documents;

// A realistic SA entry-level CV — the actual audience: someone with a diploma
// and a bit of experience who needs a clean, ATS-friendly document.
var cv = new CvDocument(
    FullName: "Thabo Mokoena",
    Headline: "Junior Software Developer",
    Contact: new CvContact(
        Email: "thabo.mokoena@example.co.za",
        Phone: "+27 82 555 0142",
        Location: "Soweto, Johannesburg",
        Links: new[] { "github.com/thabomokoena" }),
    Summary: "Motivated developer with a National Diploma in IT and hands-on experience "
           + "building offline-first Android apps in C#. Comfortable across the stack and "
           + "keen to grow in a team that ships.",
    Experience: new[]
    {
        new CvExperience("IT Support Intern", "Gauteng Community Hub", "Johannesburg", "Feb 2023", null,
            new[]
            {
                "Resolved 40+ hardware and network tickets a week, cutting average turnaround from 3 days to 1.",
                "Built a small C# tool to automate monthly asset reports, saving ~6 hours a month.",
            }),
        new CvExperience("Retail Assistant", "Shoprite", "Soweto", "Jun 2021", "Jan 2023",
            new[] { "Handled point-of-sale and daily cash-ups with zero shortfalls over 18 months." }),
    },
    Education: new[]
    {
        new CvEducation("National Diploma: Information Technology", "University of Johannesburg",
            "Johannesburg", "2020", "2022", "Distinction in Software Development."),
        new CvEducation("National Senior Certificate", "Morris Isaacson High School", "Soweto", null, "2019"),
    },
    Skills: new[] { "C#", ".NET / MAUI", "Android", "SQL", "Git", "Problem solving" },
    Certifications: new[] { new CvCertification("Microsoft Certified: Azure Fundamentals", "Microsoft", "2023") },
    Languages: new[] { "English", "isiZulu", "Sesotho" });

var engine = new PdfSharpDocumentEngine();
var result = await engine.RenderAsync(new DocumentRequest(DocumentKind.Cv, cv));

var outPath = args.Length > 0 ? args[0] : "cv-sample.pdf";
File.WriteAllBytes(outPath, result.Bytes);

var header = result.Bytes.Length >= 5
    ? System.Text.Encoding.ASCII.GetString(result.Bytes, 0, 5)
    : "(too short)";

Console.WriteLine($"file   : {Path.GetFullPath(outPath)}");
Console.WriteLine($"name   : {result.SuggestedFileName}");
Console.WriteLine($"mime   : {result.MimeType}");
Console.WriteLine($"bytes  : {result.Bytes.Length:N0}");
Console.WriteLine($"header : {header}   (expect %PDF-)");

var ok = header == "%PDF-" && result.Bytes.Length > 1000;
Console.WriteLine(ok ? "RESULT : OK — valid PDF produced" : "RESULT : FAIL");
Environment.Exit(ok ? 0 : 1);
