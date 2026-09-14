variable "tenancy_ocid" { type = string }
variable "user_ocid" { type = string }
variable "fingerprint" { type = string }

variable "private_key_path" {
  type    = string
  default = "~/.oci/oci_api_key.pem"
}

variable "region" { type = string }

variable "compartment_ocid" {
  type        = string
  description = "Root compartment equals the tenancy OCID in a fresh account."
}

variable "ssh_public_key_path" {
  type    = string
  default = "~/.ssh/id_ed25519.pub"
}

variable "availability_domain" {
  type        = string
  default     = ""
  description = "Empty uses the first AD. Set to retry a different AD when capacity is exhausted."
}

variable "instance_ocpus" {
  type    = number
  default = 2
}

variable "instance_memory_gbs" {
  type    = number
  default = 12
}
