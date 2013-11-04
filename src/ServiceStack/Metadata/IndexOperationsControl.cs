using System.Collections.Generic;
using System.Text;
using System.Web.UI;
using ServiceStack.Templates;
using ServiceStack.Web;

namespace ServiceStack.Metadata
{
    internal class IndexOperationsControl : System.Web.UI.Control
    {
        public IRequest HttpRequest { get; set; }
        public string Title { get; set; }
        public List<string> OperationNames { get; set; }
        public IDictionary<int, string> Xsds { get; set; }
        public int XsdServiceTypesIndex { get; set; }
        public MetadataPagesConfig MetadataConfig { get; set; }

        public string RenderRow(string operation)
        {
            var show = HostContext.DebugMode; //Always show in DebugMode

            // use a fully qualified path if WebHostUrl is set
            string baseUrl = HttpRequest.GetParentAbsolutePath();
            if (HostContext.Config.WebHostUrl != null)
            {
                baseUrl = HostContext.Config.WebHostUrl.CombineWith(baseUrl);
            }

            var opTemplate = new StringBuilder("<tr><th>{0}</th>");
            foreach (var config in MetadataConfig.AvailableFormatConfigs)
            {
                var uri = baseUrl.CombineWith(config.DefaultMetadataUri);
                if (MetadataConfig.IsVisible(HttpRequest, config.Format.ToFormat(), operation))
                {
                    show = true;
                    opTemplate.AppendFormat(@"<td><a href=""{0}?op={{0}}"">{1}</a></td>", uri, config.Name);
                }
                else
                    opTemplate.AppendFormat("<td>{0}</td>", config.Name);
            }

            opTemplate.Append("</tr>");

            return show ? string.Format(opTemplate.ToString(), operation) : "";
        }

        protected override void Render(HtmlTextWriter output)
        {
            var operationsPart = new TableTemplate
            {
                Title = "Operations",
                Items = this.OperationNames,
                ForEachItem = RenderRow
            }.ToString();

            var xsdsPart = new ListTemplate
            {
                Title = "XSDS:",
                ListItemsIntMap = this.Xsds,
                ListItemTemplate = @"<li><a href=""?xsd={0}"">{1}</a></li>"
            }.ToString();

            var wsdlTemplate = new StringBuilder();
            var soap11Config = MetadataConfig.GetMetadataConfig("soap11") as SoapMetadataConfig;
            var soap12Config = MetadataConfig.GetMetadataConfig("soap12") as SoapMetadataConfig;
            if (soap11Config != null || soap12Config != null)
            {
                wsdlTemplate.AppendLine("<h3>WSDLS:</h3>");
                wsdlTemplate.AppendLine("<ul>");
                if (soap11Config != null)
                {
                    wsdlTemplate.AppendFormat(
                        @"<li><a href=""{0}"">{0}</a></li>",
                        soap11Config.WsdlMetadataUri);
                }
                if (soap12Config != null)
                {
                    wsdlTemplate.AppendFormat(
                        @"<li><a href=""{0}"">{0}</a></li>",
                        soap12Config.WsdlMetadataUri);
                }
                wsdlTemplate.AppendLine("</ul>");
            }

            var debugOnlyInfo = new StringBuilder();
            if (HostContext.DebugMode)
            {
                debugOnlyInfo.Append("<h3>Debug Info:</h3>");
                debugOnlyInfo.AppendLine("<ul>");
                debugOnlyInfo.AppendLine("<li><a href=\"operations/metadata\">Operations Metadata</a></li>");
                debugOnlyInfo.AppendLine("</ul>");
            }

            var renderedTemplate = HtmlTemplates.Format(
                HtmlTemplates.GetIndexOperationsTemplate(),
                this.Title,
                this.XsdServiceTypesIndex,
                operationsPart,
                xsdsPart,
                wsdlTemplate,
                debugOnlyInfo);

            output.Write(renderedTemplate);
        }

    }
}