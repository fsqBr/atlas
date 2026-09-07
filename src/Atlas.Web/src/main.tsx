import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { App } from "./App";
import { initAuth } from "./auth";
import { I18nProvider } from "./i18n";
import { initTheme } from "./theme";
import "./styles.css";

initTheme();

// With OIDC enabled the page may redirect to the identity provider before anything renders.
initAuth().then((ready) => {
  if (!ready) return;
  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
      <I18nProvider>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </I18nProvider>
    </React.StrictMode>,
  );
});
