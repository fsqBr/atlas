using System;
using System.Web;
using System.Web.UI;

namespace Shop.Web
{
    public partial class Default : Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            var user = HttpContext.Current.User.Identity.Name;
            var cached = HttpRuntime.Cache["catalog"];
            var settings = System.Configuration.ConfigurationManager.AppSettings["mode"];
            lblUser.Text = user + settings + cached;
        }
    }
}
