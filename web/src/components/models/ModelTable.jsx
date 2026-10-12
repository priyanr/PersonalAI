import { useEffect, useState } from "react";
import { useDispatch, useSelector } from "react-redux";
import { useModels } from "@/hooks/useModels";
import { fetchModelSettingFields } from "@/store/modelsSlice";
import ModelConfigDialog from "./ModelConfigDialog";

function formatSize(bytes) {
  if (!bytes) return "";
  const gb = bytes / 1024 ** 3;
  return `${gb.toFixed(1)} GB`;
}

// Short labels for the settings a user pinned — anything left on auto isn't shown.
function settingBadges(settings) {
  if (!settings) return [];
  const badges = [];
  if (settings.OutputLimitParameter)
    badges.push(settings.OutputLimitParameter === "none" ? "no output cap" : settings.OutputLimitParameter);
  if (settings.MaxOutputTokens) badges.push(`max ${Number(settings.MaxOutputTokens).toLocaleString()} tokens`);
  if (settings.ToolCalling) badges.push(`tools ${settings.ToolCalling === "disabled" ? "off" : "on"}`);
  if (settings.ReasoningEffort) badges.push(`effort: ${settings.ReasoningEffort}`);
  if (settings.Api) badges.push(settings.Api === "responses" ? "/responses" : "/chat/completions");
  if (settings.Thinking) badges.push(`thinking ${settings.Thinking}`);
  return badges;
}

export default function ModelTable({ providerKey = "ollama" }) {
  const dispatch = useDispatch();
  const { models, status, error } = useModels(providerKey);
  const fields = useSelector((state) => state.models.settingFields[providerKey]);
  const [configuring, setConfiguring] = useState(null);

  useEffect(() => {
    dispatch(fetchModelSettingFields(providerKey));
    setConfiguring(null);
  }, [dispatch, providerKey]);

  if (status === "loading") return <p>Loading models...</p>;
  if (status === "failed") return <p className="error-text">{error}</p>;
  if (models.length === 0) return <p className="muted">No models available.</p>;

  const isOllama = providerKey === "ollama";
  const configuringModel = configuring && models.find((m) => m.name === configuring);

  return (
    <>
      <table className="model-table">
        <thead>
          <tr>
            <th>Name</th>
            {isOllama && (
              <>
                <th>Size</th>
                <th>Parameters</th>
                <th>Quantization</th>
              </>
            )}
            <th>Settings</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {models.map((m) => {
            const badges = settingBadges(m.settings);
            return (
              <tr key={m.name}>
                <td>{m.name}</td>
                {isOllama && (
                  <>
                    <td>{formatSize(m.size)}</td>
                    <td>{m.parameterSize || "—"}</td>
                    <td>{m.quantizationLevel || "—"}</td>
                  </>
                )}
                <td>
                  {badges.length === 0 ? (
                    <span className="muted">auto</span>
                  ) : (
                    <span className="model-badges">
                      {badges.map((b) => (
                        <span key={b} className="model-badge">
                          {b}
                        </span>
                      ))}
                    </span>
                  )}
                </td>
                <td className="model-row-actions">
                  <button type="button" className="secondary" disabled={!fields} onClick={() => setConfiguring(m.name)}>
                    Configure
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {configuringModel && fields && (
        <ModelConfigDialog
          providerKey={providerKey}
          model={configuringModel}
          fields={fields}
          onClose={() => setConfiguring(null)}
        />
      )}
    </>
  );
}
