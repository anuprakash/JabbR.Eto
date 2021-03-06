using System;
using Eto.Forms;
using Eto.Drawing;
using JabbR.Client;
using JabbR.Client.Models;
using System.IO;
using Eto;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JabbR.Desktop.Model;
using System.Diagnostics;
using Eto.Threading;

namespace JabbR.Desktop.Interface
{
    public abstract class MessageSection : Panel
    {
        string existingPrefix;
        string lastAutoComplete;
        int? autoCompleteIndex;
        bool autoCompleting;
        bool initialized;

        protected string LastHistoryMessageId { get; private set; }

        protected WebView History { get; private set; }

        protected TextBox TextEntry { get; private set; }

        public abstract string TitleLabel { get; }

        public virtual bool SupportsAutoComplete
        {
            get { return false; }
        }

        public virtual bool AllowNotificationCollapsing
        {
            get { return false; }
        }

        struct DelayedCommand
        {
            public string Command { get; set; }

            public object[] Parameters { get; set; }
        }

        List<DelayedCommand> delayedCommands;
        bool loaded;
        object sync = new object();

        public MessageSection()
        {
            History = new WebView();
            History.DocumentLoaded += HandleDocumentLoaded;
            TextEntry = MessageEntry();
        }

        public new void Initialize()
        {
            if (initialized)
                return;
            initialized = true;
            Content = CreateLayout();
        }

        protected virtual Control CreateLayout()
        {
            var layout = new DynamicLayout(Padding.Empty, Size.Empty);
            layout.Add(History, yscale: true);
            layout.Add(new Panel { Content = TextEntry, Padding = new Padding(10) });
            return layout;
        }

        protected virtual void HandleAction(WebViewLoadingEventArgs e)
        {
            FinishLoad();
        }

        void HandleOpenNewWindow(object sender, WebViewNewWindowEventArgs e)
        {
            Application.Instance.AsyncInvoke(() => Application.Instance.Open(e.Uri.AbsoluteUri));
            e.Cancel = true;
        }

        void HandleDocumentLoading(object sender, WebViewLoadingEventArgs e)
        {
            if (e.IsMainFrame)
            {
                Debug.Print("Loading {0}", e.Uri);
                if (e.Uri.IsFile || e.Uri.IsLoopback)
                {
                    Application.Instance.AsyncInvoke(delegate
                    {
                        HandleAction(e);
                    });
                    e.Cancel = true;
                }
                else
                {
                    Application.Instance.AsyncInvoke(delegate
                    {
                        Application.Instance.Open(e.Uri.AbsoluteUri);
                    });
                    e.Cancel = true;
                }
            }
        }

        protected void BeginLoad()
        {
            SendCommandDirect("beginLoad");
        }

        protected void FinishLoad()
        {
            SendCommandDirect("finishLoad");
        }

        public override void OnLoadComplete(EventArgs e)
        {
            base.OnLoadComplete(e);
            
            var resourcePath = EtoEnvironment.GetFolderPath(EtoSpecialFolder.ApplicationResources);
            resourcePath = Path.Combine(resourcePath, "Styles", "default");
            resourcePath += Path.DirectorySeparatorChar;
            //History.LoadHtml (File.OpenRead (Path.Combine (resourcePath, "channel.html")), new Uri(resourcePath));
            History.Url = new Uri(Path.Combine(resourcePath, "channel.html"));
        }

        protected virtual void HandleDocumentLoaded(object sender, WebViewLoadedEventArgs e)
        {
            Application.Instance.AsyncInvoke(delegate
            {
                StartLive();
                ReplayDelayedCommands();
            });
        }

        protected void StartLive()
        {
            loaded = true;
            if (Generator.ID == Generators.Wpf)
            {
                SendCommandDirect("settings", new { html5video = false });
            }
            History.DocumentLoading += HandleDocumentLoading;
            History.OpenNewWindow += HandleOpenNewWindow;
        }

        protected void ReplayDelayedCommands()
        {
            lock (sync)
            {
                if (delayedCommands != null)
                {
                    foreach (var command in delayedCommands)
                    {
                        SendCommandDirect(command.Command, command.Parameters);
                    }
                    delayedCommands = null;
                }
                loaded = true;
            }
            
            
        }

        public void AddMessage(ChannelMessage message)
        {
            SendCommand("addMessage", message);
        }

        public void AddHistory(IEnumerable<ChannelMessage> messages, bool shouldScroll = false)
        {
            SendCommand("addHistory", messages, shouldScroll);
            var last = messages.FirstOrDefault();
            if (last != null)
                LastHistoryMessageId = last.Id;
        }

        public void SetTopic(string topic)
        {
            SendCommand("setTopic", topic);
        }

        public void AddNotification(NotificationMessage notification)
        {
            SendCommand("addNotification", notification, AllowNotificationCollapsing);
        }

        public void AddMessageContent(MessageContent content)
        {
            SendCommand("addMessageContent", content);
        }

        public void SetMarker()
        {
            SendCommand("setMarker");
        }

        protected void SendCommand(string command, params object[] parameters)
        {
            string[] vals = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                vals[i] = JsonConvert.SerializeObject(parameters[i]);
            }
            var script = string.Format("JabbR.{0}({1});", command, string.Join(", ", vals));
            Application.Instance.AsyncInvoke(delegate
            {
                if (!loaded)
                {
                    lock (sync)
                    {
                        Debug.Print("*** Adding delayed command : {0}", command);
                        if (delayedCommands == null)
                            delayedCommands = new List<DelayedCommand>();
                        delayedCommands.Add(new DelayedCommand
                        {
                            Command = command,
                            Parameters = parameters
                        });
                    }
                    return;
                }
                History.ExecuteScript(script);
            });
        }

        protected void SendCommandDirect(string command, params object[] parameters)
        {
            string[] vals = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                vals[i] = JsonConvert.SerializeObject(parameters[i]);
            }
            var script = string.Format("JabbR.{0}({1});", command, string.Join(", ", vals));
            Application.Instance.Invoke(delegate
            {
                History.ExecuteScript(script);
            });
        }

        TextBox MessageEntry()
        {
            var control = new TextBox
            {
                PlaceholderText = "Send Message..."
            };
            control.KeyDown += (sender, e) =>
            {
                if (e.KeyData == Keys.Enter)
                {
                    e.Handled = true;
                    var text = control.Text;
                    control.Text = string.Empty;
                    ProcessCommand(text);
                }
                if (SupportsAutoComplete && e.KeyData == Keys.Tab)
                {
                    e.Handled = true;
                    //Debug.Print("Completing: {0}" , Thread.IsMainThread());
                    ProcessAutoComplete(control.Text);
                }
            };
            control.TextChanged += (sender, e) =>
            {
                UserTyping();
                ResetAutoComplete();
            };
            return control;
        }

        public abstract void ProcessCommand(string command);

        public virtual void UserTyping()
        {
        }

        public override void Focus()
        {
            TextEntry.Focus();
        }

        protected virtual Task<IEnumerable<string>> GetAutoCompleteNames(string search)
        {
            return null;
        }

        protected virtual void ResetAutoComplete()
        {
            existingPrefix = null;
            lastAutoComplete = null;
            autoCompleteIndex = null;
            autoCompleting = false;
        }

        public virtual async void ProcessAutoComplete(string text)
        {
            if (autoCompleting)
                return;
            autoCompleting = true;
            var index = autoCompleteIndex ?? text.LastIndexOf(' ');
            if (index > text.Length)
            {
                ResetAutoComplete();
                return;
            }
            var prefix = (index >= 0 ? text.Substring(index + 1) : text);
            if (prefix.Length > 0)
            {
                var existingText = index >= 0 ? text.Substring(0, index + 1) : string.Empty;
                
                var searchPrefix = existingPrefix ?? prefix;
                //Debug.Print("Getting auto complete names: {0}" , Thread.IsMainThread());
                var results = await GetAutoCompleteNames(searchPrefix);
                if (results == null)
                {
                    ResetAutoComplete();
                    return;
                }
                if (!autoCompleting)
                    return;

                try
                {
                    var allMatches = results.OrderBy(r => r);
                    
                    IEnumerable<string> matches = allMatches.ToArray();
                    if (!string.IsNullOrEmpty(lastAutoComplete))
                    {
                        matches = matches.Where(r => string.Compare(r, lastAutoComplete, StringComparison.CurrentCultureIgnoreCase) > 0);
                    }
                
                    var user = matches.FirstOrDefault() ?? allMatches.FirstOrDefault();
                    if (user != null)
                    {
                        //Debug.Print("Setting Text: {0}" , Thread.IsMainThread());
                        Application.Instance.Invoke(() =>
                        {
                            TextEntry.Text = existingText + TranslateAutoCompleteText(user, searchPrefix);
                            lastAutoComplete = user;
                            if (existingPrefix == null)
                            {
                                existingPrefix = prefix;
                                autoCompleteIndex = index;
                            }
                        });
                    }
                    autoCompleting = false;
                }
                catch
                {
                    ResetAutoComplete();
                }
            }
        }

        public virtual string TranslateAutoCompleteText(string selection, string search)
        {
            return selection;
        }
    }
}

