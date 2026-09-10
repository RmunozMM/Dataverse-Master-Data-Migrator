using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace DataverseMasterDataMigrator.XrmToolBox.UI
{
    /// <summary>
    /// Matches the visual pattern of Metadata Dataverse Document's own About dialog (dark header
    /// with title/version, white body with description + developer/contact links).
    /// </summary>
    internal sealed class AboutForm : Form
    {
        public AboutForm()
        {
            Text = "About Dataverse Master Data Migrator";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 400);

            var version = Assembly.GetExecutingAssembly().GetName().Version;

            var header = new Panel { Dock = DockStyle.Top, Height = 90, BackColor = ColorTranslator.FromHtml("#152238") };
            header.Controls.Add(new Label
            {
                Text = "Dataverse Master Data Migrator",
                ForeColor = Color.White,
                Font = new Font(FontFamily.GenericSansSerif, 15, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(20, 18)
            });
            var versionRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(20, 55),
                BackColor = Color.Transparent
            };
            versionRow.Controls.Add(new Label
            {
                Text = $"Version {version}",
                ForeColor = Color.Silver,
                Font = new Font(FontFamily.GenericSansSerif, 9),
                AutoSize = true,
                Margin = new Padding(0)
            });
            versionRow.Controls.Add(new Label
            {
                Text = " | ",
                ForeColor = Color.Silver,
                Font = new Font(FontFamily.GenericSansSerif, 9),
                AutoSize = true,
                Margin = new Padding(0)
            });
            var repoLink = new LinkLabel
            {
                Text = "Enlace al repositorio",
                LinkColor = Color.Silver,
                ActiveLinkColor = Color.White,
                VisitedLinkColor = Color.Silver,
                Font = new Font(FontFamily.GenericSansSerif, 9),
                AutoSize = true,
                Margin = new Padding(0),
                BackColor = Color.Transparent
            };
            repoLink.LinkClicked += (s, e) =>
            {
                try { Process.Start("https://github.com/RmunozMM/Dataverse-Master-Data-Migrator"); }
                catch { /* no default handler registered for this link type — nothing sensible to do about it here */ }
            };
            versionRow.Controls.Add(repoLink);
            header.Controls.Add(versionRow);

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(20),
                BackColor = Color.White
            };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            body.Controls.Add(new Label
            {
                Text = "Migra datos maestros entre ambientes de Microsoft Dataverse / Dynamics 365 usando perfiles " +
                       "reutilizables: valida dependencias y lookups (Preflight), compara la estructura Source/Target, " +
                       "previsualiza los cambios y ejecuta la migración multipass con reintento de fallos — en vez de " +
                       "migraciones manuales tabla por tabla.",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 66,
                Margin = new Padding(0, 0, 0, 10)
            });

            body.Controls.Add(new Label
            {
                Text = "Desarrollador: Rogelio Muñoz",
                Font = new Font(FontFamily.GenericSansSerif, 8.25f, FontStyle.Bold),
                AutoSize = true,
                Dock = DockStyle.Top
            });
            body.Controls.Add(new Label
            {
                Text = $"Copyright © {DateTime.Now.Year} Rogelio Muñoz. Todos los derechos reservados.",
                Font = new Font(FontFamily.GenericSansSerif, 8.25f, FontStyle.Bold),
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 10)
            });

            body.Controls.Add(MakeLinkRow("Sitio Web:", "www.rogeliomunoz.cl", "https://www.rogeliomunoz.cl"));
            body.Controls.Add(MakeLinkRow("Contacto:", "rmunoz1612@gmail.com", "mailto:rmunoz1612@gmail.com"));
            body.Controls.Add(MakeLinkRow("Repositorio:", "github.com/RmunozMM/Dataverse-Master-Data-Migrator", "https://github.com/RmunozMM/Dataverse-Master-Data-Migrator"));

            var closeButton = new Button { Text = "Cerrar", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
            closeButton.Location = new Point(ClientSize.Width - closeButton.Width - 20, ClientSize.Height - closeButton.Height - 15);
            closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            Controls.Add(header);
            Controls.Add(body);
            Controls.Add(closeButton);
            closeButton.BringToFront();

            AcceptButton = closeButton;
            CancelButton = closeButton;
        }

        private static Control MakeLinkRow(string label, string linkText, string target)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 4) };
            row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 3, 6, 0) });

            var link = new LinkLabel { Text = linkText, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
            link.LinkClicked += (s, e) =>
            {
                try { Process.Start(target); }
                catch { /* no default handler registered for this link type — nothing sensible to do about it here */ }
            };
            row.Controls.Add(link);

            return row;
        }
    }
}
