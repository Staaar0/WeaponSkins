using System.Net;
using System.Text.Json;

namespace WeaponSkins;

public sealed class ApiClient : IDisposable
{
	private readonly string? baseUrl;
	private readonly string? language;
	private readonly string? dataDirectory;
	private readonly HttpClient? http;

	public ApiClient(ApiConfig config)
	{
		baseUrl = config.BaseUrl.TrimEnd('/');
		language = string.IsNullOrWhiteSpace(config.Language) ? "en" : config.Language;
		// The catalog is ~26 MB of JSON; accepting gzip/brotli cuts the download to a few MB.
		http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
		{
			Timeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds))
		};
		http.DefaultRequestHeaders.UserAgent.ParseAdd("WeaponSkins");
	}

	public ApiClient(string dataDirectory)
	{
		this.dataDirectory = dataDirectory;
	}

	public async Task<JsonDocument> Fetch(string file)
	{
		Stream stream;
		if (dataDirectory != null)
			stream = File.OpenRead(Path.Combine(dataDirectory, $"{file}.json"));
		else
			stream = await http!.GetStreamAsync($"{baseUrl}/{language}/{file}.json");

		await using (stream)
			return await JsonDocument.ParseAsync(stream);
	}

	public void Dispose()
	{
		http?.Dispose();
	}
}
