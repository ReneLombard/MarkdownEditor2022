using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using Markdig.Renderers;
using Markdig.Syntax;
using mshtml;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using WebBrowser = System.Windows.Controls.WebBrowser;

namespace MarkdownEditor2022
{
    public class Browser : IDisposable
    {
        private readonly string _file;
        private readonly Document _document;
        private HTMLDocument _htmlDocument;
        private readonly int _zoomFactor;
        private int _currentViewLine;
        private double _cachedPosition = 0,
                       _cachedHeight = 0,
                       _positionPercentage = 0;


        [ThreadStatic]
        private static StringWriter _htmlWriterStatic;

        public Browser(string file, Document document)
        {
            _zoomFactor = GetZoomFactor();
            _file = file;
            _document = document;
            _currentViewLine = -1;

            InitBrowser();
        }

        public WebBrowser Control { get; private set; }

        private void InitBrowser()
        {
            Control = new WebBrowser
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            Control.LoadCompleted += (s, e) =>
            {
                Zoom(_zoomFactor);
                _htmlDocument = (HTMLDocument)Control.Document;

                _cachedHeight = _htmlDocument.body.offsetHeight;
                _htmlDocument.documentElement.setAttribute("scrollTop", _positionPercentage * _cachedHeight / 100);

                AdjustAnchors();
            };

            // Open external links in default browser
            Control.Navigating += (s, e) =>
            {
                if (e.Uri == null)
                {
                    return;
                }

                e.Cancel = true;

                // If it's a file-based anchor we converted, open the related file if possible
                if (e.Uri.Scheme == "about")
                {
                    var file = e.Uri.LocalPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

                    if (file == "blank")
                    {
                        var fragment = e.Uri.Fragment?.TrimStart('#');
                        NavigateToFragment(fragment);
                        return;
                    }

                    if (!File.Exists(file))
                    {
                        string ext = null;

                        // If the file has no extension, see if one exists with a markdown extension.  If so,
                        // treat it as the file to open.
                        //if (string.IsNullOrEmpty(Path.GetExtension(file)))
                        //{
                        //    ext = LanguageFactory. ContentTypeDefinition.MarkdownExtensions.FirstOrDefault(fx => File.Exists(file + fx));
                        //}

                        if (ext != null)
                        {
                            VS.Documents.OpenInPreviewTabAsync(file + ext).FireAndForget();
                        }
                    }
                    else
                    {
                        VS.Documents.OpenInPreviewTabAsync(file).FireAndForget();
                    }
                }
                else if (e.Uri.IsAbsoluteUri && e.Uri.Scheme.StartsWith("http"))
                {
                    Process.Start(e.Uri.ToString());
                }
            };
        }

        private void NavigateToFragment(string fragmentId)
        {
            IHTMLElement element = _htmlDocument.getElementById(fragmentId);
            element.scrollIntoView(true);
        }

        /// <summary>
        /// Adjust the file-based anchors so that they are navigable on the local file system
        /// </summary>
        /// <remarks>Anchors using the "file:" protocol appear to be blocked by security settings and won't work.
        /// If we convert them to use the "about:" protocol so that we recognize them, we can open the file in
        /// the <c>Navigating</c> event handler.</remarks>
        private void AdjustAnchors()
        {
            try
            {
                foreach (IHTMLElement link in _htmlDocument.links)
                {
                    if (link is HTMLAnchorElement anchor && anchor.protocol == "file:")
                    {
                        string pathName = null, hash = anchor.hash;

                        // Anchors with a hash cause a crash if you try to set the protocol without clearing the
                        // hash and path name first.
                        if (hash != null)
                        {
                            pathName = anchor.pathname;
                            anchor.hash = null;
                            anchor.pathname = string.Empty;
                        }

                        anchor.protocol = "about:";

                        if (hash != null)
                        {
                            // For an in-page section link, use "blank" as the path name.  These don't work
                            // anyway but this is the proper way to handle them.
                            if (pathName == null || pathName.EndsWith("/"))
                            {
                                pathName = "blank";
                            }

                            anchor.pathname = pathName;
                            anchor.hash = hash;
                        }
                    }
                }
            }
            catch
            {
                // Ignore exceptions
            }
        }

        private static int GetZoomFactor()
        {
            using (var g = Graphics.FromHwnd(Process.GetCurrentProcess().MainWindowHandle))
            {
                var baseLine = 96;
                var dpi = g.DpiX;

                if (baseLine == dpi)
                {
                    return 100;
                }

                // 150% scaling => 225
                // 250% scaling => 400

                double scale = dpi * ((dpi - baseLine) / baseLine + 1);
                return Convert.ToInt32(Math.Ceiling(scale / 25)) * 25; // round up to nearest 25
            }
        }

        public Task UpdatePositionAsync(int line)
        {
            return ThreadHelper.JoinableTaskFactory.StartOnIdle(() =>
            {
                _currentViewLine = _document.Markdown.FindClosestLine(line);
                SyncNavigation();
            }, VsTaskRunContext.UIThreadIdlePriority).Task;
        }

        private void SyncNavigation()
        {
            if (_htmlDocument != null && AdvancedOptions.Instance.EnableScrollSync)
            {
                if (_currentViewLine == 0)
                {
                    // Forces the preview window to scroll to the top of the document
                    _htmlDocument.documentElement.setAttribute("scrollTop", 0);
                }
                else
                {
                    IHTMLElement element = _htmlDocument.getElementById("pragma-line-" + _currentViewLine);
                    if (element != null)
                    {
                        element.scrollIntoView(true);
                    }
                }
            }
            else if (_htmlDocument != null)
            {
                _currentViewLine = -1;
                _cachedPosition = _htmlDocument.documentElement.getAttribute("scrollTop");
                _cachedHeight = Math.Max(1.0, _htmlDocument.body.offsetHeight);
                _positionPercentage = _cachedPosition * 100 / _cachedHeight;
            }
        }

        public Task UpdateBrowserAsync()
        {
            return ThreadHelper.JoinableTaskFactory.StartOnIdle(() =>
            {
                // Generate the HTML document
                string html = null;
                StringWriter htmlWriter = null;
                try
                {
                    htmlWriter = (_htmlWriterStatic ??= new StringWriter());
                    htmlWriter.GetStringBuilder().Clear();

                    var htmlRenderer = new HtmlRenderer(htmlWriter);
                    Document.Pipeline.Setup(htmlRenderer);
                    htmlRenderer.UseNonAsciiNoEscape = true;
                    htmlRenderer.Render(_document.Markdown);

                    htmlWriter.Flush();
                    html = htmlWriter.ToString();
                }
                catch (Exception ex)
                {
                    // We could output this to the exception pane of VS?
                    // Though, it's easier to output it directly to the browser
                    html = "<p>An unexpected exception occurred:</p><pre>" +
                           ex.ToString().Replace("<", "&lt;").Replace("&", "&amp;") + "</pre>";
                }
                finally
                {
                    // Free any resources allocated by HtmlWriter
                    htmlWriter?.GetStringBuilder().Clear();
                }

                IHTMLElement content = null;

                if (_htmlDocument != null)
                {
                    content = _htmlDocument.getElementById("___markdown-content___");
                }

                // Content may be null if the Refresh context menu option is used.  If so, reload the template.
                if (content != null)
                {
                    content.innerHTML = html;

                    // Makes sure that any code blocks get syntax highlighted by Prism
                    IHTMLWindow2 win = _htmlDocument.parentWindow;
                    //try { win.execScript("Prism.highlightAll();", "javascript"); } catch { }
                    try { win.execScript("if (typeof onMarkdownUpdate == 'function') onMarkdownUpdate();", "javascript"); } catch { }

                    // Adjust the anchors after and edit
                    AdjustAnchors();
                }
                else
                {
                    var htmlTemplate = GetHtmlTemplate();
                    html = string.Format(CultureInfo.InvariantCulture, "{0}", html);
                    html = htmlTemplate.Replace("[content]", html);
                    Control.NavigateToString(html);
                }

                SyncNavigation();
            }, VsTaskRunContext.UIThreadBackgroundPriority).Task;
        }

        public static string GetFolder()
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            return Path.GetDirectoryName(assembly);
        }

        private static string GetHtmlTemplateFileNameFromResource()
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var assemblyDir = Path.GetDirectoryName(assembly);

            return Path.Combine(assemblyDir, "Margin\\md-template.html");
        }

        private string GetHtmlTemplate()
        {
            var baseHref = Path.GetDirectoryName(_file).Replace("\\", "/");
            var folder = GetFolder();
            var cssHighlightPath = Path.Combine(folder, "margin\\highlight.css");

            var defaultHeadBeg = $@"
<head>
    <meta http-equiv=""X-UA-Compatible"" content=""IE=Edge"" />
    <meta charset=""utf-8"" />
    <base href=""file:///{baseHref}/"" />
    <link rel=""stylesheet"" href=""{cssHighlightPath}"" />
";
            var defaultContent = $@"
    <div id=""___markdown-content___"" class=""markdown-body"">
        [content]
    </div>
";

            var templateFileName = GetHtmlTemplateFileNameFromResource();
            var template = File.ReadAllText(templateFileName);
            return template
                .Replace("<head>", defaultHeadBeg)
                .Replace("[content]", defaultContent)
                .Replace("[title]", "Markdown Preview");
        }

        private void Zoom(int zoomFactor)
        {
            if (zoomFactor == 100)
            {
                return;
            }

            dynamic OLECMDEXECOPT_DODEFAULT = 0;
            dynamic OLECMDID_OPTICAL_ZOOM = 63;
            FieldInfo fiComWebBrowser = typeof(WebBrowser).GetField("_axIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic);

            if (fiComWebBrowser == null)
            {
                return;
            }

            var objComWebBrowser = fiComWebBrowser.GetValue(Control);

            if (objComWebBrowser == null)
            {
                return;
            }

            objComWebBrowser.GetType().InvokeMember("ExecWB", BindingFlags.InvokeMethod, null, objComWebBrowser, new object[] {
                OLECMDID_OPTICAL_ZOOM,
                OLECMDEXECOPT_DODEFAULT,
                zoomFactor,
                IntPtr.Zero
            });
        }

        public void Dispose()
        {
            if (Control != null)
            {
                Control.Dispose();
            }

            _htmlDocument = null;
        }
    }
}
