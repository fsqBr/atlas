{{- define "atlas.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "atlas.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s" .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "atlas.tag" -}}
{{- default .Chart.AppVersion .Values.image.tag -}}
{{- end -}}

{{- define "atlas.labels" -}}
app.kubernetes.io/name: {{ include "atlas.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ include "atlas.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "atlas.selector" -}}
app.kubernetes.io/name: {{ include "atlas.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "atlas.secretName" -}}
{{- if .Values.secrets.existingSecret -}}{{ .Values.secrets.existingSecret }}{{- else -}}{{ include "atlas.fullname" . }}-secrets{{- end -}}
{{- end -}}

{{- define "atlas.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}{{ default (include "atlas.fullname" .) .Values.serviceAccount.name }}{{- else -}}{{ default "default" .Values.serviceAccount.name }}{{- end -}}
{{- end -}}

{{- define "atlas.dbHost" -}}
{{- if .Values.postgresql.bundled -}}{{ include "atlas.fullname" . }}-postgres{{- else -}}{{ required "postgresql.external.host is required when postgresql.bundled=false" .Values.postgresql.external.host }}{{- end -}}
{{- end -}}

{{- define "atlas.dbPort" -}}
{{- if .Values.postgresql.bundled -}}5432{{- else -}}{{ .Values.postgresql.external.port }}{{- end -}}
{{- end -}}

{{/* Environment shared by api and worker: connection string (password from the secret), keys, paths. */}}
{{- define "atlas.sharedEnv" -}}
- name: ATLAS_DB_PASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "atlas.secretName" . }}
      key: dbPassword
- name: ConnectionStrings__AtlasDb
  value: "Host={{ include "atlas.dbHost" . }};Port={{ include "atlas.dbPort" . }};Database={{ .Values.postgresql.database }};Username={{ .Values.postgresql.username }};Password=$(ATLAS_DB_PASSWORD)"
- name: Atlas__Secrets__HmacKeyBase64
  valueFrom:
    secretKeyRef:
      name: {{ include "atlas.secretName" . }}
      key: hmacKey
- name: Atlas__Secrets__MasterKeyBase64
  valueFrom:
    secretKeyRef:
      name: {{ include "atlas.secretName" . }}
      key: masterKey
- name: Atlas__Vulnerabilities__OsvBundlePath
  value: /var/atlas/vulndata/nuget-osv.json
- name: Atlas__Uploads__Directory
  value: /var/atlas/uploads
- name: Atlas__Operations__JsonLogs
  value: "true"
{{- range $i, $root := .Values.persistence.localSources }}
- name: Atlas__LocalSources__Roots__{{ $i }}__Path
  value: /sources/{{ $root.name }}
- name: Atlas__LocalSources__Roots__{{ $i }}__Label
  value: {{ $root.label | default $root.name | quote }}
{{- end }}
{{- end -}}

{{- define "atlas.localSourceMounts" -}}
{{- range .Values.persistence.localSources }}
- name: src-{{ .name }}
  mountPath: /sources/{{ .name }}
  readOnly: true
{{- end }}
{{- end -}}

{{- define "atlas.localSourceVolumes" -}}
{{- range .Values.persistence.localSources }}
- name: src-{{ .name }}
{{- if .hostPath }}
  hostPath:
    path: {{ .hostPath }}
    type: Directory
{{- else if .existingClaim }}
  persistentVolumeClaim:
    claimName: {{ .existingClaim }}
    readOnly: true
{{- else }}
  emptyDir: {}
{{- end }}
{{- end }}
{{- end -}}
