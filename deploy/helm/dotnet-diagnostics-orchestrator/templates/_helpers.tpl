{{- define "dotnet-diagnostics-orchestrator.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := include "dotnet-diagnostics-orchestrator.name" . -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.namespace" -}}
{{- default .Release.Namespace .Values.namespace -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.labels" -}}
helm.sh/chart: {{ include "dotnet-diagnostics-orchestrator.chart" . }}
app.kubernetes.io/name: {{ include "dotnet-diagnostics-orchestrator.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/component: orchestrator
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.selectorLabels" -}}
app.kubernetes.io/name: {{ include "dotnet-diagnostics-orchestrator.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "dotnet-diagnostics-orchestrator.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.secretName" -}}
{{- if .Values.bearerToken.existingSecret -}}
{{- .Values.bearerToken.existingSecret -}}
{{- else -}}
{{- printf "%s-bearer" (include "dotnet-diagnostics-orchestrator.fullname" .) -}}
{{- end -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.image" -}}
{{- $tag := default .Chart.AppVersion .Values.image.tag -}}
{{- printf "%s:%s" .Values.image.repository $tag -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.ephemeralImage" -}}
{{- default (include "dotnet-diagnostics-orchestrator.image" .) .Values.orchestrator.ephemeralContainerImage -}}
{{- end -}}

{{- define "dotnet-diagnostics-orchestrator.defaultNamespace" -}}
{{- if .Values.orchestrator.defaultNamespace -}}
{{- .Values.orchestrator.defaultNamespace -}}
{{- else if gt (len .Values.orchestrator.allowedNamespaces) 0 -}}
{{- index .Values.orchestrator.allowedNamespaces 0 -}}
{{- else -}}
{{- include "dotnet-diagnostics-orchestrator.namespace" . -}}
{{- end -}}
{{- end -}}
