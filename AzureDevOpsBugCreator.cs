    /// <summary>
    /// Azure DevOps project name where the bug should be created.
    /// </summary>
    private readonly string project;

    /// <summary>
    /// Personal Access Token used for authentication.
    /// </summary>
    private readonly string personalAccessToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureDevOpsBugCreator"/> class.
    /// Reads configuration from appsettings.json in the current working directory.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown when appsettings.json does not exist in the current working directory.</exception>
    /// <exception cref="IOException">Thrown when there was an error reading appsettings.json.</exception>
    /// <exception cref="JsonException">Thrown when appsettings.json could not be parsed as JSON.</exception>
    /// <exception cref="ArgumentException">Thrown when required configuration values are missing or empty.</exception>
    public AzureDevOpsBugCreator()
    {
        try
        {
            // Read configuration file from the current working directory
            var configPath = "appsettings.json";
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException($"Configuration file '{configPath}' not found in '{Environment.CurrentDirectory}'.");
            }

            var config = File.ReadAllText(configPath);

            // Parse into a JObject to safely access properties and provide clearer errors when missing
            JObject settings = JsonConvert.DeserializeObject<JObject>(config)
                               ?? throw new JsonException("appsettings.json deserialized to null.");

            azureDevOpsUrl = settings.Value<string>("AzureDevOpsUrl")?.Trim();
            project = settings.Value<string>("Project")?.Trim();
            personalAccessToken = settings.Value<string>("PersonalAccessToken")?.Trim();

            // Validate required values
            if (string.IsNullOrWhiteSpace(azureDevOpsUrl))
                throw new ArgumentException("AzureDevOpsUrl is missing or empty in appsettings.json.");

            if (string.IsNullOrWhiteSpace(project))
                throw new ArgumentException("Project is missing or empty in appsettings.json.");

            if (string.IsNullOrWhiteSpace(personalAccessToken))
                throw new ArgumentException("PersonalAccessToken is missing or empty in appsettings.json. For security, consider using an environment variable or secret store instead.");
        }
        catch (FileNotFoundException)
        {
            // Let the caller decide how to handle missing configuration; include current directory for debugging
            throw;
        }
        catch (JsonException ex)
        {
            throw new JsonException("Failed to parse appsettings.json. Ensure it is valid JSON and contains AzureDevOpsUrl, Project, and PersonalAccessToken properties.", ex);
        }
        catch (IOException ex)
        {
            throw new IOException("Error reading appsettings.json.", ex);
        }
    }

    /// <summary>
    /// Creates a Bug work item in Azure DevOps with the provided title.
    /// </summary>
    /// <param name="bugTitle">The title to set on the created bug work item.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bugTitle"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the Azure DevOps API responds with a non-success status code.</exception>
    /// <exception cref="Exception">Propagates other unexpected exceptions that occur during request construction or execution.</exception>
    public void CreateBug(string bugTitle)
    {
        if (string.IsNullOrWhiteSpace(bugTitle))
            throw new ArgumentException("bugTitle must not be null or empty.", nameof(bugTitle));

        // Build the REST API URL for creating a Bug work item
        var url = $"{azureDevOpsUrl.TrimEnd('/')}/{project}/_apis/wit/workitems/$Bug?api-version=6.0";

        // Prepare the RestSharp client and request
        var client = new RestClient(url);
        var request = new RestRequest(Method.POST);

        // Azure DevOps requires the Content-Type for work item patch operations to be "application/json-patch+json"
        request.AddHeader("Content-Type", "application/json-patch+json");

        // Create a Basic Authorization header using the PAT.
        // Azure DevOps accepts basic auth with empty username and PAT as the password.
        // The header value is "Basic <base64(':PAT')>"
        try
        {
            string authToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{personalAccessToken}"));
            request.AddHeader("Authorization", $"Basic {authToken}");
        }
        catch (Exception ex)
        {
            // Encoding/base64 errors are unexpected but should be surfaced with context
            throw new Exception("Failed to create Authorization header from PersonalAccessToken.", ex);
        }

        // Build the JSON Patch document: an array of operations.
        // Each operation includes an "op" (add), a "path" (which field), and the "value".
        // Fields set here are Title, Description, AssignedTo, and ReproSteps.
        var bugData = new[]
        {
            new { op = "add", path = "/fields/System.Title", value = bugTitle },
            new { op = "add", path = "/fields/System.Description", value = "Bug created automatically due to failed Selenium test." },
            new { op = "add", path = "/fields/System.AssignedTo", value = "shahab@tecoholic.com" },
            new { op = "add", path = "/fields/Microsoft.VSTS.TCM.ReproSteps", value = "See attached logs for detailed error." }
        };

        // Serialize the JSON Patch array and add it to the request body.
        request.AddParameter("application/json-patch+json", JsonConvert.SerializeObject(bugData), ParameterType.RequestBody);

        IRestResponse? response = null;
        try
        {
            // Execute the request synchronously.
            // Note: in newer applications prefer using an async API and HttpClient/IHttpClientFactory.
            response = client.Execute(request);
        }
        catch (Exception ex)
        {
            // Network/serialization/RestSharp errors end up here. Include the target URL for easier debugging.
            throw new Exception($"Failed to execute request to Azure DevOps at '{url}'. See inner exception for details.", ex);
        }

        // If response is null something unexpected happened; surface that as an error.
        if (response == null)
        {
            throw new Exception("No response received from Azure DevOps API.");
        }

        // Evaluate the response. On failure, include status code and content to aid diagnosis.
        if (response.IsSuccessful)
        {
            // Optionally, parse response.Content to read created work item id or url.
            Console.WriteLine("Bug created successfully in Azure DevOps.");
        }
        else
        {
            // Build a helpful error message. response.ErrorMessage may be null, include ResponseStatus, StatusCode and Content.
            var status = response.ResponseStatus;
            var statusCode = response.StatusCode;
            var content = response.Content;

            var errorMessage = $"Failed to create bug. Response status: {status}. HTTP status code: {(int)statusCode} ({statusCode}).";
            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
            {
                errorMessage += $" ErrorMessage: {response.ErrorMessage}.";
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                // Content may contain JSON error details returned by Azure DevOps; include it for diagnosis but avoid logging secrets.
                errorMessage += $" Response content: {content}";
            }

            // Throw an InvalidOperationException so callers can catch and handle API-level failures specifically.
            throw new InvalidOperationException(errorMessage);
        }
    }
}
