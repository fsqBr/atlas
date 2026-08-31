using System;
using System.Data.SqlClient;

namespace ThirdParty
{
    // Vendored code: excluded by .atlasignore, so nothing below may produce findings.
    public class Noise
    {
        public string Cpf { get; set; }
        public string Senha { get; set; }

        public void Run(string id)
        {
            var cmd = new SqlCommand("select * from t where id = " + id);
            Console.WriteLine("cpf=" + Cpf);
        }
    }
}
