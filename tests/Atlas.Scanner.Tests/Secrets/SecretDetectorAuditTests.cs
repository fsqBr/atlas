using Atlas.Scanner.Secrets;

namespace Atlas.Scanner.Tests.Secrets;

/// <summary>Regressions for the 2026-08 rule audit: the generic credential detector missed every
/// prefixed name (word boundary vs '_'/camelCase) and every unquoted assignment.</summary>
public class SecretDetectorAuditTests
{
    private static bool Matches(string detectorId, string line) =>
        SecretDetectors.All.Single(d => d.Id == detectorId).Pattern.IsMatch(line);

    [Theory]
    [InlineData("password = \"S3cretV@lue99!\"")]
    [InlineData("db_password = \"S3cretV@lue99!\"")]
    [InlineData("var dbPassword = \"S3cretV@lue99!\";")]
    [InlineData("\"db_password\": \"S3cretV@lue99!\"")]
    [InlineData("aws_secret_access_key: \"wJalrXUtnFEMI1234567890\"")]
    public void Prefixed_and_quoted_key_credentials_match(string line) =>
        Assert.True(Matches("secrets.generic-assignment", line));

    [Theory]
    [InlineData("PASSWORD=hunter2secret!")]
    [InlineData("DB_PASSWORD=hunter2secret!")]
    [InlineData("  api_key: sk-something-long-enough")]
    [InlineData("export CLIENT_SECRET=abcdef123456")]
    public void Unquoted_env_and_yaml_assignments_match(string line) =>
        Assert.True(Matches("secrets.generic-assignment-unquoted", line));

    [Theory]
    [InlineData("PASSWORD=$DB_PASSWORD")]
    [InlineData("PASSWORD=${DB_PASSWORD}")]
    [InlineData("PASSWORD=%DB_PASSWORD%")]
    public void Unquoted_detector_ignores_variable_references(string line) =>
        Assert.False(Matches("secrets.generic-assignment-unquoted", line));

    [Theory]
    [InlineData("secrets.anthropic-api-key", "sk-ant-api03-abcdefghijklmnopqrstuv")]
    [InlineData("secrets.openai-api-key", "sk-proj-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("secrets.gitlab-token", "glpat-abcdefghijklmnopqrst")]
    [InlineData("secrets.npm-token", "npm_abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Provider_token_formats_are_recognized(string detectorId, string token) =>
        Assert.True(Matches(detectorId, "key = " + token));
}
