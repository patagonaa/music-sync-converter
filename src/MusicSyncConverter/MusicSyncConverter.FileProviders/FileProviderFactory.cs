using FileProviders.WebDav;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using System;
using System.Net;

namespace MusicSyncConverter.FileProviders
{
    public class FileProviderFactory
    {
        public IFileProvider Get(string uriString)
        {
            var splitUri = uriString.Split(':');
            if (splitUri.Length < 2)
                throw new ArgumentException("Uri must contain protocol");

            switch (splitUri[0])
            {
                case "file":
                    {
                        return new PhysicalFileProvider(uriString.Replace("file://", ""));
                    }
                case "http":
                case "https":
                    {
                        var uri = new Uri(uriString);
                        var creds = ParseUsernamePassword(uri.UserInfo);
                        var config = new WebDavConfiguration
                        {
                            BaseUri = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped),
                            User = creds.UserName,
                            Password = creds.Password,
                        };
                        return new WebDavFileProvider(new OptionsWrapper<WebDavConfiguration>(config));
                    }
                default:
                    throw new ArgumentException($"Invalid URI Scheme: {splitUri[0]}");
            }
        }

        private NetworkCredential ParseUsernamePassword(string userPass)
        {
            var split = userPass.Split(':');
            return new NetworkCredential(split[0], split[1]);
        }
    }
}
